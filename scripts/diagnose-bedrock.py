#!/usr/bin/env python3
"""Read-only Bedrock diagnostics. Never emits credentials or response bodies.

By default this makes no network requests. --check-network sends an empty JSON
object to Converse; it does NOT test inference or prove model authorization.
--check-inference instead sends one fixed, one-token InvokeModel request.
--list-models reads the regional foundation-model and system-profile catalogs;
listing never tests inference or proves model access.
Short-term format: aws/aws-bedrock-token-generator-js, src/token.ts.
"""

import argparse
import base64
import datetime as dt
import json
import os
import re
import selectors
import signal
import socket
import ssl
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

MAX_CAPTURE = 1024 * 1024
MAX_TOKEN = 64 * 1024
MAX_CATALOG_RESPONSE = 1024 * 1024
MAX_CATALOG_PAGES = 5
MAX_CATALOG_ITEMS = 5000
MAX_CATALOG_CURSOR = 2048
TOKEN_KEY = "AWS_BEARER_TOKEN_BEDROCK"
DEFAULT_MODEL = "global.anthropic.claude-fable-5-1"
# Commercial AWS partition only. No user-supplied hosts, partitions or URLs.
REGIONS = frozenset("""
af-south-1 ap-east-1 ap-east-2 ap-south-1 ap-south-2
ap-southeast-1 ap-southeast-2 ap-southeast-3 ap-southeast-4
ap-southeast-5 ap-southeast-6 ap-southeast-7
ap-northeast-1 ap-northeast-2 ap-northeast-3
ca-central-1 ca-west-1 eu-central-1 eu-central-2
eu-west-1 eu-west-2 eu-west-3 eu-north-1 eu-south-1 eu-south-2
il-central-1 me-south-1 me-central-1 mx-central-1 sa-east-1
us-east-1 us-east-2 us-west-1 us-west-2
""".split())
REGION_PATTERN = re.compile(r"(?:af|ap|ca|eu|il|me|mx|sa|us)-[a-z]+-[1-9][0-9]?")
MODEL_PATTERN = re.compile(
    r"(?:(?:apac|us|eu|global)\.)?anthropic\.claude-[a-z0-9-]+(?::[0-9]+)?"
)
# Match the AWS denial sentence, not separate keywords or quoted policy advice.
SCP_INVOKE_DENIAL_PATTERN = re.compile(
    r"User:\s+\S+\s+is not authorized to perform:?\s+bedrock:(?P<action>InvokeModel|InvokeModelWithResponseStream)"
    r"(?:\s+on resource:\s+\S+)?\s+(?:with|because of) an explicit deny in a service control policy"
    r"(?::\s+arn:aws:organizations::[0-9]{12}:policy/o-[a-z0-9]{10,32}/service_control_policy/p-[a-z0-9]{8,128})?\.?",
    re.IGNORECASE,
)
ENDPOINT_KEYS = (
    "ANTHROPIC_BEDROCK_BASE_URL", "AWS_ENDPOINT_URL",
    "AWS_ENDPOINT_URL_BEDROCK", "AWS_ENDPOINT_URL_BEDROCK_RUNTIME",
    "ANTHROPIC_BASE_URL", "ANTHROPIC_UNIX_SOCKET", "ANTHROPIC_BEDROCK_MANTLE_BASE_URL",
)
PROXY_KEYS = ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy")
ERROR_OUTCOMES = {
    "ValidationException": "validation_response_inference_not_verified",
    "UnrecognizedClientException": "server_rejected_authentication",
    "ExpiredTokenException": "server_reports_expired_credential",
    "InvalidSignatureException": "server_rejected_signature",
    "AccessDeniedException": "access_denied_key_validity_unknown",
    "ServiceQuotaExceededException": "service_quota_exceeded",
    "ThrottlingException": "request_throttled",
    "ResourceNotFoundException": "resource_not_found",
}


def token_metadata(token, now=None):
    """Return public metadata only; unknown format never means invalid."""
    now = now or dt.datetime.now(dt.timezone.utc)
    result = {"present": bool(token), "kind": "unknown", "accessVerified": False}
    if not token:
        result["kind"] = "missing"
        return result
    if len(token) > MAX_TOKEN:
        result["inspection"] = "size_limit"
        return result
    if token != token.strip() or any(c.isspace() for c in token):
        result["containsWhitespace"] = True
    if token.lower().startswith("bearer "):
        result["hasAuthorizationHeaderPrefix"] = True
        return result
    if token.startswith("sk-ant-api03-"):
        result["kind"] = "anthropic_api_key"
        return result
    if token.startswith("ABSK"):
        # Heuristic only; AWS publishes no supported offline expiry decoder.
        result["kind"] = "possible_long_term_bedrock_key"
        result["expiration"] = "unknown"
        return result
    if not token.startswith("bedrock-api-key-"):
        return result
    result["kind"] = "short_term_bedrock_candidate"
    result["inspection"] = "unrecognized_format"
    try:
        decoded = base64.b64decode(token[16:], validate=True).decode("utf-8")
        # The official generator omits https:// before encoding.
        url = urllib.parse.urlsplit("https://" + decoded)
        if (url.netloc != "bedrock.amazonaws.com" or url.path != "/"
                or url.fragment or any(c.isspace() for c in decoded)):
            return result
        rows = urllib.parse.parse_qsl(url.query, keep_blank_values=True, strict_parsing=True)
        fields = dict(rows)
        if len(fields) != len(rows):
            return result
        if (fields.get("Version") != "1"
                or fields.get("Action") != "CallWithBearerToken"
                or fields.get("X-Amz-Algorithm") != "AWS4-HMAC-SHA256"):
            return result
        stamp = fields.get("X-Amz-Date", "")
        if not re.fullmatch(r"[0-9]{8}T[0-9]{6}Z", stamp):
            return result
        issued = dt.datetime.strptime(stamp, "%Y%m%dT%H%M%SZ").replace(tzinfo=dt.timezone.utc)
        if issued.strftime("%Y%m%dT%H%M%SZ") != stamp:
            return result
        seconds = fields.get("X-Amz-Expires", "")
        if not re.fullmatch(r"[0-9]{1,5}", seconds) or not 1 <= int(seconds) <= 43200:
            return result
        scope = fields.get("X-Amz-Credential", "").split("/")
        if (len(scope) != 5 or not scope[0] or scope[1] != stamp[:8]
                or not REGION_PATTERN.fullmatch(scope[2])
                or scope[3:] != ["bedrock", "aws4_request"]):
            return result
        expires = issued + dt.timedelta(seconds=int(seconds))
        result.update({
            "kind": "short_term_bedrock", "inspection": "parsed",
            "region": scope[2], "nominalExpiresAt": expires.isoformat().replace("+00:00", "Z"),
            "expiration": "expired" if now >= expires else "before_nominal_expiry",
            "mayExpireEarlierWithIssuingSession": True,
        })
    except (ValueError, UnicodeError, OverflowError):
        pass
    return result


def parse_environment(data, marker):
    begin, end = b"\0" + marker + b"_BEGIN\0", b"\0" + marker + b"_END\0"
    if data.count(begin) != 1 or data.count(end) != 1:
        return None
    start, finish = data.index(begin) + len(begin), data.index(end)
    if finish <= start:
        return None
    framed = data[start:finish]
    if not framed.endswith(b"\0"):
        return None
    env = {}
    for row in framed[:-1].split(b"\0"):
        key, separator, value = row.partition(b"=")
        if not separator or not re.fullmatch(rb"[A-Za-z_][A-Za-z0-9_]*", key):
            return None
        name = key.decode("ascii")
        if name in env:
            return None
        env[name] = os.fsdecode(value)
    return env if "PATH" in env else None


def fresh_shell_environment(base, timeout=6):
    """Capture shell startup noise and environment without ever printing it."""
    marker = ("MIGHTY_BEDROCK_" + uuid.uuid4().hex).encode("ascii")
    tag = marker.decode("ascii")
    command = "printf '\\0" + tag + "_BEGIN\\0'; /usr/bin/env -0; printf '\\0" + tag + "_END\\0'"
    proc = None
    try:
        proc = subprocess.Popen(
            ["/bin/zsh", "-ilc", command], env=base, cwd=str(Path.home()),
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            start_new_session=True,
        )
        output, total = bytearray(), 0
        deadline = time.monotonic() + timeout
        with selectors.DefaultSelector() as selector:
            selector.register(proc.stdout, selectors.EVENT_READ, True)
            selector.register(proc.stderr, selectors.EVENT_READ, False)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None, "timeout"
                for key, _ in selector.select(min(remaining, 0.1)):
                    chunk = os.read(key.fd, 65536)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    total += len(chunk)
                    if total > MAX_CAPTURE:
                        return None, "size_limit"
                    if key.data:
                        output.extend(chunk)
            remaining = deadline - time.monotonic()
            if remaining <= 0 or proc.wait(timeout=remaining) != 0:
                return None, "shell_failed"
        env = parse_environment(bytes(output), marker)
        return (env, "loaded") if env is not None else (None, "invalid_capture")
    except subprocess.TimeoutExpired:
        return None, "timeout"
    except (OSError, ValueError):
        return None, "unavailable"
    finally:
        if proc is not None:
            if proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except OSError:
                    pass
            try:
                proc.wait(timeout=1)
            except (OSError, subprocess.TimeoutExpired):
                pass
            for stream in (proc.stdout, proc.stderr):
                if stream:
                    stream.close()


def read_user_settings(environment, home=None):
    home = Path(home) if home is not None else Path.home()
    directory = environment.get("CLAUDE_CONFIG_DIR") or str(home / ".claude")
    directory = Path(directory).expanduser()
    if not directory.is_absolute():
        directory = home / directory
    descriptor = None
    try:
        descriptor = os.open(directory / "settings.json", os.O_RDONLY | os.O_NONBLOCK)
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_CAPTURE:
            return {}, "unsupported_or_too_large"
        with os.fdopen(descriptor, "rb") as stream:
            descriptor = None
            data = stream.read(MAX_CAPTURE + 1)
        if len(data) > MAX_CAPTURE:
            return {}, "size_limit"
        settings = json.loads(data)
        values = settings.get("env", {}) if isinstance(settings, dict) else None
        if not isinstance(values, dict):
            return {}, "invalid_format"
        return {key: value for key, value in values.items()
                if isinstance(key, str) and isinstance(value, str)}, "loaded"
    except FileNotFoundError:
        return {}, "absent"
    except (OSError, ValueError, UnicodeError, RecursionError):
        return {}, "unreadable_or_invalid"
    finally:
        if descriptor is not None:
            os.close(descriptor)


def network_target(region, model):
    if region not in REGIONS or len(model) > 128 or not MODEL_PATTERN.fullmatch(model):
        return None
    return "https://bedrock-runtime." + region + ".amazonaws.com/model/" + model + "/converse"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def classify_response(status, body, headers=None, inference=False):
    result = {"httpStatus": status, "inferenceTested": False, "accessVerified": False}
    if inference:
        result.update(inferenceRequested=True, inferenceSucceeded=False)
    if 300 <= status < 400:
        result["outcome"] = "redirect_blocked"
        return result
    try:
        parsed = json.loads(body[:65536])
    except (ValueError, UnicodeError, RecursionError):
        parsed = None
    lowered = body[:65536].lower()
    authentication_rejected = status >= 400 and any(fragment in lowered for fragment in (
        b"authentication failed", b"invalid api key", b"please make sure your api key is valid",
    ))
    if status >= 400:
        for fragments, reason in (
            ((b"model identifier", b"invalid"), "invalid_model_identifier"),
            ((b"on-demand throughput", b"inference profile"), "inference_profile_required"),
            ((b"not supported", b"api"), "unsupported_api"),
            ((b"max_tokens",), "max_tokens_rejected"),
        ):
            if all(fragment in lowered for fragment in fragments):
                result["reason"] = reason
                break
    candidates = [headers.get("x-amzn-errortype", "")] if headers is not None else []
    if isinstance(parsed, dict):
        candidates.extend(parsed.get(key, "") for key in ("__type", "code", "Code"))
        if (inference and status == 200 and parsed.get("type") == "message"
                and parsed.get("role") == "assistant" and isinstance(parsed.get("content"), list)
                and parsed.get("stop_reason") in ("end_turn", "max_tokens", "stop_sequence")):
            result.update(inferenceTested=True, inferenceSucceeded=True, accessVerified=True,
                          outcome="inference_succeeded")
            return result
    for candidate in candidates:
        if isinstance(candidate, str):
            name = candidate.split(":", 1)[0].rsplit("#", 1)[-1]
            if name in ERROR_OUTCOMES:
                # AccessDenied also carries an explicit authentication rejection
                # for some bearer-token failures. Preserve its type without
                # hiding that narrower response or guessing the underlying cause.
                outcome = ("server_rejected_authentication"
                           if name == "AccessDeniedException" and authentication_rejected
                           else ERROR_OUTCOMES[name])
                message = (parsed.get("Message") or parsed.get("message")) if isinstance(parsed, dict) else None
                scp_denial = SCP_INVOKE_DENIAL_PATTERN.fullmatch(message.strip()) if isinstance(message, str) else None
                if (name == "AccessDeniedException" and status == 403 and not authentication_rejected
                        and scp_denial):
                    outcome = "service_control_policy_denied"
                    result["reason"] = ("explicit_deny_bedrock_invoke_model_with_response_stream"
                        if scp_denial.group("action").lower() == "invokemodelwithresponsestream"
                        else "explicit_deny_bedrock_invoke_model")
                result.update(errorType=name, outcome=outcome)
                return result
    if b"expired" in lowered and (b"token" in lowered or b"credential" in lowered):
        outcome = "server_reports_expired_credential"
    elif authentication_rejected:
        outcome = "server_rejected_authentication"
    elif b"accessdenied" in lowered or b"access denied" in lowered:
        outcome = "access_denied_key_validity_unknown"
    elif status == 400 and (b"validation" in lowered or b"messages" in lowered):
        outcome = "validation_response_inference_not_verified"
    elif status == 401:
        outcome = "unauthorized_reason_unconfirmed"
    elif status == 403:
        outcome = "forbidden_reason_unconfirmed"
    elif 200 <= status < 300:
        outcome = "response_received_inference_not_verified"
    else:
        outcome = "http_error_reason_unconfirmed"
    result["outcome"] = outcome
    return result


def bearer_header_error(token):
    if not token or len(token) > MAX_TOKEN or any(c.isspace() for c in token):
        return "missing_or_unusable_header_value"
    try:
        token.encode("ascii")
    except UnicodeEncodeError:
        return "unusable_header_value"
    return None


def check_network(token, region, model, inference=False):
    target = network_target(region, model)
    if not target:
        return {"outcome": "unsupported_region_or_model", "requestSent": False}
    header_error = bearer_header_error(token)
    if header_error:
        return {"outcome": header_error, "requestSent": False}
    try:
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), NoRedirect(),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()),
        )
        request = urllib.request.Request(
            target.removesuffix("/converse") + "/invoke" if inference else target,
            data=json.dumps({"anthropic_version": "bedrock-2023-05-31", "max_tokens": 1,
                             "messages": [{"role": "user", "content": "Reply OK."}]}).encode()
            if inference else b"{}", method="POST",
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
        )
        with opener.open(request, timeout=30 if inference else 10) as response:
            return classify_response(response.status, response.read(65536), response.headers, inference)
    except urllib.error.HTTPError as error:
        try:
            return classify_response(error.code, error.read(65536), error.headers, inference)
        finally:
            error.close()
    except urllib.error.URLError as error:
        reason = error.reason
        category = ("dns_failed" if isinstance(reason, socket.gaierror)
                    else "tls_failed" if isinstance(reason, ssl.SSLError)
                    else "timeout" if isinstance(reason, (TimeoutError, socket.timeout))
                    else "network_failed")
        return {"outcome": category, "inferenceTested": False, "accessVerified": False}
    except (TimeoutError, socket.timeout):
        return {"outcome": "timeout", "inferenceTested": False, "accessVerified": False}
    except Exception:
        # Exception messages may contain a URL/header/body. Never serialize them.
        return {"outcome": "request_failed", "inferenceTested": False, "accessVerified": False}


def catalog_page(opener, token, target):
    """Return a bounded in-memory page and public status; never echo page text."""
    status = {"inferenceTested": False, "accessVerified": False}
    try:
        request = urllib.request.Request(target, method="GET", headers={
            "Authorization": "Bearer " + token, "Accept": "application/json",
        })
        with opener.open(request, timeout=10) as response:
            status["httpStatus"] = response.status
            body = response.read(MAX_CATALOG_RESPONSE + 1)
            if len(body) > MAX_CATALOG_RESPONSE:
                status["outcome"] = "response_size_limit"
                return status, None
            if response.status != 200:
                return classify_response(response.status, body, response.headers), None
        try:
            parsed = json.loads(body)
        except (ValueError, UnicodeError, RecursionError):
            parsed = None
        if not isinstance(parsed, dict):
            status["outcome"] = "invalid_catalog_response"
            return status, None
        status["outcome"] = "catalog_response_received"
        return status, parsed
    except urllib.error.HTTPError as error:
        status["httpStatus"] = error.code
        body = b""
        try:
            body = error.read(65536)
        except Exception:
            status["responseBodyRead"] = "failed"
        finally:
            try:
                error.close()
            except Exception:
                status["responseClose"] = "failed"
        status.update(classify_response(error.code, body, error.headers))
        return status, None
    except urllib.error.URLError as error:
        reason = error.reason
        status["outcome"] = ("dns_failed" if isinstance(reason, socket.gaierror)
                             else "tls_failed" if isinstance(reason, ssl.SSLError)
                             else "timeout" if isinstance(reason, (TimeoutError, socket.timeout))
                             else "network_failed")
    except (TimeoutError, socket.timeout):
        status["outcome"] = "timeout"
    except Exception:
        status["outcome"] = "request_failed"
    return status, None


def public_catalog_entry(row, profiles):
    if not isinstance(row, dict):
        return None
    value = row.get("inferenceProfileId" if profiles else "modelId")
    if not isinstance(value, str) or len(value) > 128 or not MODEL_PATTERN.fullmatch(value):
        return None
    if profiles:
        if (value.startswith("anthropic.") or row.get("type") != "SYSTEM_DEFINED"
                or row.get("status") != "ACTIVE"):
            return None
        return {"id": value, "status": "ACTIVE"}
    return value if value.startswith("anthropic.") else None


def fetch_catalog(opener, token, endpoint, profiles=False):
    """Read independent catalogs; a global profile need not have a local base ID."""
    output_key = "profiles" if profiles else "modelIds"
    response_key = "inferenceProfileSummaries" if profiles else "modelSummaries"
    path = "/inference-profiles" if profiles else "/foundation-models"
    query = {"type": "SYSTEM_DEFINED", "maxResults": 1000} if profiles else {"byProvider": "Anthropic"}
    result = {output_key: [], "complete": False, "pagesFetched": 0, "filteredEntries": 0}
    seen_ids, seen_cursors = set(), set()
    scanned = 0
    for page_number in range(MAX_CATALOG_PAGES if profiles else 1):
        status, page = catalog_page(opener, token, endpoint + path + "?" + urllib.parse.urlencode(query))
        result["fetchStatus"] = status
        if page is None:
            result["incompleteReason"] = ("response_body_read_failed"
                if status.get("responseBodyRead") == "failed" else status["outcome"])
            break
        rows = page.get(response_key)
        if not isinstance(rows, list):
            status["outcome"] = "invalid_catalog_response"
            result["incompleteReason"] = status["outcome"]
            break
        result["pagesFetched"] += 1
        remaining = MAX_CATALOG_ITEMS - scanned
        for row in rows[:remaining]:
            entry = public_catalog_entry(row, profiles)
            if entry is None:
                result["filteredEntries"] += 1
                continue
            identifier = entry["id"] if profiles else entry
            if identifier not in seen_ids:
                seen_ids.add(identifier)
                result[output_key].append(entry)
        scanned += min(len(rows), remaining)
        if len(rows) > remaining:
            result["incompleteReason"] = "catalog_item_limit"
            break
        # Like the AWS SDK, treat absent/null/empty tokens as the last page.
        cursor = page.get("nextToken")
        if not profiles or cursor is None or cursor == "":
            result["complete"] = True
            break
        if (not isinstance(cursor, str) or not 1 <= len(cursor) <= MAX_CATALOG_CURSOR
                or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in cursor)):
            result["incompleteReason"] = "invalid_pagination_cursor"
            break
        try:
            cursor.encode("utf-8")
        except UnicodeEncodeError:
            result["incompleteReason"] = "invalid_pagination_cursor"
            break
        if cursor in seen_cursors:
            result["incompleteReason"] = "repeated_pagination_cursor"
            break
        if scanned >= MAX_CATALOG_ITEMS:
            result["incompleteReason"] = "catalog_item_limit"
            break
        if page_number + 1 >= MAX_CATALOG_PAGES:
            result["incompleteReason"] = "pagination_page_limit"
            break
        seen_cursors.add(cursor)
        query["nextToken"] = cursor
    result[output_key].sort(key=lambda entry: entry["id"] if profiles else entry)
    return result


def list_models(token, region):
    result = {"inferenceTested": False, "accessVerified": False, "complete": False}
    if region not in REGIONS:
        return {**result, "outcome": "unsupported_region", "requestSent": False}
    header_error = bearer_header_error(token)
    if header_error:
        return {**result, "outcome": header_error, "requestSent": False}
    try:
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), NoRedirect(),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()),
        )
    except Exception:
        return {**result, "outcome": "request_failed", "requestSent": False}
    endpoint = "https://bedrock." + region + ".amazonaws.com"
    result["foundationModels"] = fetch_catalog(opener, token, endpoint)
    result["inferenceProfiles"] = fetch_catalog(opener, token, endpoint, profiles=True)
    result["complete"] = all(result[key]["complete"] for key in ("foundationModels", "inferenceProfiles"))
    return result


def build_report(current, fresh, shell_status, settings, settings_status, do_network=False,
                 model=DEFAULT_MODEL, do_inference=False, do_list_models=False):
    shell = fresh if fresh is not None else current
    effective = {**shell, **settings}
    current_token, shell_token = current.get(TOKEN_KEY, ""), shell.get(TOKEN_KEY, "")
    settings_token, effective_token = settings.get(TOKEN_KEY, ""), effective.get(TOKEN_KEY, "")
    report = {
        "readOnly": True, "settingsWritten": False, "inferenceTested": False,
        "shellCapture": shell_status, "userSettings": settings_status,
        "scope": "terminal_and_user_settings_only_project_and_managed_settings_not_inspected",
        "currentTerminalToken": token_metadata(current_token),
        "freshShellToken": token_metadata(shell_token) if fresh is not None else {"inspection": "unavailable"},
        "userSettingsToken": token_metadata(settings_token),
        "effectiveUserSettingsOverShellToken": token_metadata(effective_token),
        "effectiveTokenSource": "user_settings" if TOKEN_KEY in settings else "shell",
        "currentMatchesFreshShell": current_token == shell_token if fresh is not None else None,
        "freshShellMatchesSettings": shell_token == settings_token if fresh is not None and TOKEN_KEY in settings else None,
        "networkRequested": do_network or do_inference or do_list_models,
        "requestedModel": model if isinstance(model, str) and len(model) <= 128
        and MODEL_PATTERN.fullmatch(model) else "missing_or_unsupported",
    }
    region = effective.get("AWS_REGION") or effective.get("AWS_DEFAULT_REGION") or ""
    report["effectiveRegion"] = region if region in REGIONS else "missing_or_unsupported"
    known_region = report["effectiveUserSettingsOverShellToken"].get("region")
    report["tokenRegionMatchesEffectiveRegion"] = known_region == region if known_region and region in REGIONS else None
    if do_inference:
        report.update(inferenceRequested=True, inferenceSucceeded=False)
    if do_list_models:
        report["catalogRequested"] = True
        report.pop("requestedModel")  # Catalog mode does not select or invoke a target model.
    if not (do_network or do_inference or do_list_models):
        return report
    if sum(bool(mode) for mode in (do_network, do_inference, do_list_models)) > 1:
        report["network"] = {"outcome": "skipped_conflicting_network_modes"}
        return report
    if fresh is None or settings_status not in ("loaded", "absent"):
        report["network"] = {"outcome": "skipped_incomplete_environment_or_settings"}
        return report
    if any(env.get(key) for env in (shell, effective) for key in ENDPOINT_KEYS):
        report["network"] = {"outcome": "skipped_custom_endpoint_unsupported"}
        return report
    if any(env.get(key) for env in (shell, effective) for key in PROXY_KEYS):
        report["network"] = {"outcome": "skipped_proxy_unsupported"}
        return report
    if any(env.get("CLAUDE_CODE_USE_MANTLE", "").lower() in ("1", "true")
           for env in (shell, effective)):
        report["network"] = {"outcome": "skipped_mantle_routing_unsupported"}
        return report
    if do_list_models:
        report["catalog"] = list_models(effective_token, region)
        return report
    result = check_network(effective_token, region, model, inference=do_inference)
    report["network"] = {"effectiveUserSettingsOverShell": result}
    if do_inference:
        result.setdefault("inferenceRequested", True)
        result.setdefault("inferenceSucceeded", False)
        report["inferenceTested"] = result.get("inferenceTested", False)
        report["inferenceSucceeded"] = result["inferenceSucceeded"]
    if not do_inference and shell_token != effective_token and shell_token:
        report["network"]["freshShellSameConfiguredRegion"] = check_network(shell_token, region, model)
    return report


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, message):
        self.exit(2, "인수를 확인하세요. --help로 사용법을 볼 수 있습니다.\n")


def main():
    parser = SafeArgumentParser(description="키 값을 출력하지 않는 읽기 전용 Bedrock 진단")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check-network", action="store_true", help="AWS에 빈 요청을 보내 오류 종류 확인 (모델 추론 아님)")
    mode.add_argument("--check-inference", action="store_true", help="고정된 짧은 문장으로 최대 1 토큰 모델 호출 1회 (요금 발생 가능)")
    mode.add_argument("--list-models", action="store_true", help="리전의 공개 Claude 기본 모델·시스템 추론 프로필 조회 (추론 아님)")
    parser.add_argument("--model", default=DEFAULT_MODEL, help="공개 Claude Bedrock 모델 ID")
    args = parser.parse_args()
    if len(args.model) > 128 or not MODEL_PATTERN.fullmatch(args.model):
        parser.error("invalid model")
    current = dict(os.environ)
    fresh, status = fresh_shell_environment(current)
    settings, settings_status = read_user_settings(fresh if fresh is not None else current)
    report = build_report(current, fresh, status, settings, settings_status,
                          args.check_network, args.model, args.check_inference, args.list_models)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print('{"outcome":"cancelled"}')
        sys.exit(130)
    except Exception:
        print('{"outcome":"diagnostic_failed_without_details","settingsWritten":false}')
        sys.exit(1)
