import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch
import urllib.error
import urllib.parse

spec = importlib.util.spec_from_file_location(
    "diagnose_bedrock", Path(__file__).resolve().parents[1] / "diagnose-bedrock.py")
diagnostic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostic)


class DiagnosticTests(unittest.TestCase):
    def test_explicit_scp_invoke_denial_has_specific_public_reason(self):
        for action, reason in (("InvokeModel", "explicit_deny_bedrock_invoke_model"),
                               ("InvokeModelWithResponseStream", "explicit_deny_bedrock_invoke_model_with_response_stream")):
            for suffix in ("", ": arn:aws:organizations::123456789012:policy/o-0123456789/service_control_policy/p-01234567"):
                for connector in ("with", "because of"):
                    message = ("User: arn:aws:sts::123456789012:assumed-role/private-role/private-session "
                        "is not authorized to perform: bedrock:" + action + " on resource: "
                        "arn:aws:bedrock:ap-northeast-2::foundation-model/private-model "
                        + connector + " an explicit deny in a service control policy" + suffix)
                    for headers, extra in (({"x-amzn-errortype": "AccessDeniedException:private-detail"}, {}),
                                           ({}, {"__type": "private.namespace#AccessDeniedException"})):
                        with self.subTest(action=action, suffix=bool(suffix), connector=connector, body_type=bool(extra)):
                            result = diagnostic.classify_response(403, json.dumps({"Message": message, **extra}).encode(),
                                                                  headers, inference=True)
                            self.assertEqual(result["errorType"], "AccessDeniedException")
                            self.assertEqual(result["outcome"], "service_control_policy_denied")
                            self.assertEqual(result["reason"], reason)
                            self.assertFalse(result["inferenceSucceeded"])
                            self.assertFalse(result["inferenceTested"])
                            self.assertFalse(result["accessVerified"])
                            for private in ("private-", "123456789012", "arn:", "o-0123456789", "p-01234567"):
                                self.assertNotIn(private, json.dumps(result))

    def test_scp_mentions_and_other_failures_keep_existing_classification(self):
        denied = ("User: fixture is not authorized to perform: bedrock:InvokeModel on resource: fixture "
                  "with an explicit deny in a service control policy")
        cases = [
            (403, "AccessDeniedException", "Not authorized; see service control policy documentation for explicit deny examples.", "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", "For example: " + denied, "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied.replace("service control policy", "identity-based policy") + ". Check service control policy documentation.", "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied.replace("InvokeModel", "ListFoundationModels"), "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied.replace("InvokeModel", "InvokeModelWithResponseStreamExtra"), "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied + ": arn:aws:iam::123456789012:policy/private", "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied + ". See service control policy documentation.", "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied + ": https://private.invalid/policy", "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", denied.replace("with an explicit deny in", "because no permission is granted by"), "access_denied_key_validity_unknown"),
            (400, "AccessDeniedException", denied, "access_denied_key_validity_unknown"),
            (403, "AccessDeniedException", "Authentication failed: Please make sure your API Key is valid. " + denied, "server_rejected_authentication"),
            (403, "ExpiredTokenException", denied, "server_reports_expired_credential"),
            (403, None, "Forbidden. See service control policy documentation.", "forbidden_reason_unconfirmed"),
            (403, None, denied, "forbidden_reason_unconfirmed"),
        ]
        for status, error_type, message, outcome in cases:
            with self.subTest(status=status, error_type=error_type, outcome=outcome, message=message):
                headers = {"x-amzn-errortype": error_type} if error_type else {}
                result = diagnostic.classify_response(status, json.dumps({"Message": message}).encode(), headers)
                self.assertEqual(result["outcome"], outcome)
                self.assertNotEqual(result.get("reason"), "explicit_deny_bedrock_invoke_model")
                self.assertFalse(result["accessVerified"])
        result = diagnostic.classify_response(403, json.dumps({"Message": "Not authorized", "documentation": denied}).encode(),
                                              {"x-amzn-errortype": "AccessDeniedException"})
        self.assertEqual(result["outcome"], "access_denied_key_validity_unknown")

    def test_explicit_authentication_rejection_preserves_error_type(self):
        message = "Authentication failed: Please make sure your API Key is valid."
        for headers, body in (
            ({"x-amzn-errortype": "AccessDeniedException:private-detail"},
             {"Message": message}),
            ({}, {"__type": "private.namespace#AccessDeniedException", "message": message}),
            ({}, {"code": "AccessDeniedException", "Message": "Invalid API key"}),
        ):
            with self.subTest(headers=headers, body=body):
                result = diagnostic.classify_response(403, json.dumps(body).encode(), headers,
                                                      inference=True)
                self.assertEqual(result["errorType"], "AccessDeniedException")
                self.assertEqual(result["outcome"], "server_rejected_authentication")
                self.assertFalse(result["inferenceSucceeded"])
                self.assertFalse(result["accessVerified"])
                self.assertNotIn("private", json.dumps(result))
                self.assertNotIn(message, json.dumps(result))

    def test_generic_access_denied_does_not_diagnose_the_key(self):
        for message in ("Access denied", "Not authorized to invoke this model",
                        "The API key is valid but this model is not authorized"):
            with self.subTest(message=message):
                result = diagnostic.classify_response(403, json.dumps({"Message": message}).encode(),
                    {"x-amzn-errortype": "AccessDeniedException"})
                self.assertEqual(result["errorType"], "AccessDeniedException")
                self.assertEqual(result["outcome"], "access_denied_key_validity_unknown")
                self.assertFalse(result["accessVerified"])

    def test_public_requested_model_is_reported_without_reading_configuration(self):
        self.assertEqual(diagnostic.DEFAULT_MODEL, "global.anthropic.claude-fable-5-1")
        base = {diagnostic.TOKEN_KEY: "fixture", "AWS_REGION": "ap-northeast-2"}
        with patch.object(diagnostic, "check_network") as network:
            report = diagnostic.build_report(base, base, "loaded", {}, "absent")
            self.assertEqual(report["requestedModel"], "global.anthropic.claude-fable-5-1")
            custom = "us.anthropic.claude-sonnet-4-6"
            report = diagnostic.build_report(base, base, "loaded", {}, "absent", model=custom)
            self.assertEqual(report["requestedModel"], custom)
            report = diagnostic.build_report(base, base, "loaded", {}, "absent", model="private-secret")
            self.assertNotIn("private-secret", json.dumps(report))
            network.assert_not_called()

    def test_exception_whitelist_and_no_response_leaks(self):
        for name, outcome in diagnostic.ERROR_OUTCOMES.items():
            for headers, body in (
                ({"x-amzn-errortype": name + ":private-detail"}, b"private-body"),
                ({}, json.dumps({"__type": "private.namespace#" + name,
                                 "message": "private-body"}).encode()),
                ({}, json.dumps({"code": name}).encode()),
            ):
                with self.subTest(name=name):
                    result = diagnostic.classify_response(400, body, headers)
                    self.assertEqual(result["errorType"], name)
                    self.assertEqual(result["outcome"], outcome)
                    self.assertFalse(result["accessVerified"])
                    self.assertNotIn("private", json.dumps(result))
        result = diagnostic.classify_response(400, b'{"code":"private-secret"}',
                                               {"x-amzn-errortype": "private-secret"})
        self.assertNotIn("private", json.dumps(result))
        self.assertEqual(result["outcome"], "http_error_reason_unconfirmed")
        result = diagnostic.classify_response(400, b'{"message":"private model identifier is invalid"}',
                                               {"x-amzn-errortype": "ValidationException"})
        self.assertEqual(result["reason"], "invalid_model_identifier")
        self.assertNotIn("private", json.dumps(result))

    def test_only_valid_inference_response_verifies_access(self):
        valid = {"type": "message", "role": "assistant", "content": [], "stop_reason": "max_tokens"}
        self.assertTrue(diagnostic.classify_response(200, json.dumps(valid).encode(), inference=True)["accessVerified"])
        for status, body, inference in (
            (200, b"{}", True), (200, b"not-json", True),
            (200, json.dumps(valid).encode(), False),
            (403, json.dumps(valid).encode(), True),
            (200, json.dumps({**valid, "role": "user"}).encode(), True),
        ):
            self.assertFalse(diagnostic.classify_response(status, body, inference=inference)["accessVerified"])

    def test_inference_exactly_one_fixed_request_with_effective_key(self):
        shell = {diagnostic.TOKEN_KEY: "shell-fixture", "AWS_REGION": "ap-northeast-2"}
        settings = {diagnostic.TOKEN_KEY: "settings-fixture"}
        response = MagicMock()
        response.status, response.headers = 200, {}
        response.read.return_value = b'{"type":"message","role":"assistant","content":[],"stop_reason":"max_tokens"}'
        opener = MagicMock()
        opener.open.return_value.__enter__.return_value = response
        with patch.object(diagnostic.urllib.request, "build_opener", return_value=opener):
            report = diagnostic.build_report(shell, shell, "loaded", settings, "loaded", do_inference=True)
        opener.open.assert_called_once()
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, "https://bedrock-runtime.ap-northeast-2.amazonaws.com/model/"
                         "global.anthropic.claude-fable-5-1/invoke")
        self.assertEqual(report["requestedModel"], "global.anthropic.claude-fable-5-1")
        self.assertEqual(opener.open.call_args.kwargs["timeout"], 30)
        self.assertEqual(request.get_header("Authorization"), "Bearer settings-fixture")
        self.assertEqual(json.loads(request.data), {"anthropic_version": "bedrock-2023-05-31",
            "max_tokens": 1, "messages": [{"role": "user", "content": "Reply OK."}]})
        self.assertTrue(report["inferenceSucceeded"])
        self.assertFalse(report["effectiveUserSettingsOverShellToken"]["accessVerified"])
        self.assertTrue(report["network"]["effectiveUserSettingsOverShell"]["accessVerified"])
        self.assertNotIn("settings-fixture", json.dumps(report))

    def test_http_error_header_is_classified_without_retry(self):
        error = urllib.error.HTTPError("https://invalid.example", 400, "private-message",
            {"x-amzn-errortype": "ExpiredTokenException"}, io.BytesIO(b"private-body"))
        opener = MagicMock()
        opener.open.side_effect = error
        with patch.object(diagnostic.urllib.request, "build_opener", return_value=opener):
            result = diagnostic.check_network("fixture", "ap-northeast-2", diagnostic.DEFAULT_MODEL, True)
        opener.open.assert_called_once()
        self.assertTrue(result["inferenceRequested"])
        self.assertFalse(result["inferenceSucceeded"])
        self.assertEqual(result["errorType"], "ExpiredTokenException")
        self.assertNotIn("private", json.dumps(result))

    def test_offline_and_unsupported_routing_send_no_requests(self):
        base = {diagnostic.TOKEN_KEY: "fixture", "AWS_REGION": "ap-northeast-2"}
        with patch.object(diagnostic, "check_network") as network:
            diagnostic.build_report(base, base, "loaded", {}, "absent")
            for override in ({"HTTPS_PROXY": "https://example.invalid"},
                             {"CLAUDE_CODE_USE_MANTLE": "1"},
                             {"ANTHROPIC_BEDROCK_MANTLE_BASE_URL": "https://example.invalid"}):
                env = {**base, **override}
                report = diagnostic.build_report(env, env, "loaded", {}, "absent", do_inference=True)
                self.assertFalse(report["inferenceSucceeded"])
            network.assert_not_called()
        for region, model in (("invalid", diagnostic.DEFAULT_MODEL), ("ap-northeast-2", "../private")):
            self.assertFalse(diagnostic.check_network("fixture", region, model, True)["requestSent"])


class CatalogTests(unittest.TestCase):
    base = {diagnostic.TOKEN_KEY: "shell-fixture", "AWS_REGION": "ap-northeast-2"}
    global_id = "global.anthropic.claude-fable-5-1"
    apac_id = "apac.anthropic.claude-sonnet-4-6"
    base_id = "anthropic.claude-sonnet-4-6"

    @staticmethod
    def response(payload, status=200):
        response = MagicMock()
        response.status, response.headers = status, {}
        response.read.return_value = payload if isinstance(payload, bytes) else json.dumps(payload).encode()
        response.__enter__.return_value = response
        return response

    @staticmethod
    def profile(model, **extra):
        return {"inferenceProfileId": model, "status": "ACTIVE", "type": "SYSTEM_DEFINED", **extra}

    def run_catalog(self, *responses):
        opener = MagicMock()
        opener.open.side_effect = responses
        with patch.object(diagnostic.urllib.request, "build_opener", return_value=opener) as factory:
            result = diagnostic.list_models("settings-fixture", "ap-northeast-2")
        return result, opener, factory

    def test_catalog_pagination_preserves_global_ids_and_effective_key_without_inference(self):
        cursor = "private-cursor+/="
        responses = [
            self.response({"modelSummaries": [
                {"modelId": self.base_id, "providerName": "Anthropic", "modelLifecycle": {"status": "LEGACY"}},
                {"modelId": self.base_id, "modelName": "private-name"},
                {"modelId": "arn:aws:bedrock:ap-northeast-2:123456789012:private-model"},
                {"modelId": "amazon.private-model"},
            ]}),
            self.response({"inferenceProfileSummaries": [
                self.profile(self.global_id, inferenceProfileArn="private-arn", description="private-description"),
                self.profile("arn:aws:bedrock:ap-northeast-2:123456789012:private-profile"),
                self.profile(self.apac_id, type="APPLICATION"),
                self.profile(self.apac_id, status="private-status"),
            ], "nextToken": cursor}),
            self.response({"inferenceProfileSummaries": [
                self.profile(self.apac_id), self.profile(self.global_id),
            ]}),
        ]
        opener = MagicMock()
        opener.open.side_effect = responses
        with patch.object(diagnostic.urllib.request, "build_opener", return_value=opener) as factory, \
                patch.object(diagnostic, "check_network") as inference:
            report = diagnostic.build_report(self.base, self.base, "loaded",
                {diagnostic.TOKEN_KEY: "settings-fixture"}, "loaded", do_list_models=True)
        catalog = report["catalog"]
        self.assertTrue(report["networkRequested"])
        self.assertTrue(report["catalogRequested"])
        self.assertTrue(catalog["complete"])
        self.assertEqual(catalog["foundationModels"]["modelIds"], [self.base_id])
        self.assertEqual(catalog["inferenceProfiles"]["profiles"], [
            {"id": self.apac_id, "status": "ACTIVE"}, {"id": self.global_id, "status": "ACTIVE"}])
        self.assertEqual(catalog["inferenceProfiles"]["pagesFetched"], 2)
        self.assertFalse(report["inferenceTested"])
        self.assertFalse(catalog["inferenceTested"])
        self.assertFalse(catalog["accessVerified"])
        self.assertNotIn("inferenceSucceeded", report)
        inference.assert_not_called()
        self.assertEqual(opener.open.call_count, 3)
        urls = [call.args[0].full_url for call in opener.open.call_args_list]
        self.assertEqual(urls[0], "https://bedrock.ap-northeast-2.amazonaws.com/foundation-models?byProvider=Anthropic")
        self.assertEqual(urls[1], "https://bedrock.ap-northeast-2.amazonaws.com/inference-profiles?type=SYSTEM_DEFINED&maxResults=1000")
        self.assertEqual(urllib.parse.parse_qs(urllib.parse.urlsplit(urls[2]).query),
                         {"type": ["SYSTEM_DEFINED"], "maxResults": ["1000"], "nextToken": [cursor]})
        for call in opener.open.call_args_list:
            request = call.args[0]
            self.assertEqual(request.get_method(), "GET")
            self.assertIsNone(request.data)
            self.assertEqual(request.get_header("Authorization"), "Bearer settings-fixture")
            self.assertEqual(urllib.parse.urlsplit(request.full_url).netloc, "bedrock.ap-northeast-2.amazonaws.com")
        handlers = factory.call_args.args
        self.assertTrue(any(isinstance(handler, diagnostic.NoRedirect) for handler in handlers))
        self.assertEqual(next(handler.proxies for handler in handlers
                              if isinstance(handler, diagnostic.urllib.request.ProxyHandler)), {})
        tls_context = next(handler._context for handler in handlers
                           if isinstance(handler, diagnostic.urllib.request.HTTPSHandler))
        self.assertEqual(tls_context.verify_mode, diagnostic.ssl.CERT_REQUIRED)
        self.assertTrue(tls_context.check_hostname)
        rendered = json.dumps(report)
        for secret in ("private-", "123456789012", "shell-fixture", "settings-fixture", "arn:"):
            self.assertNotIn(secret, rendered)

    def test_catalog_partial_failure_preserves_other_catalog_and_auth_classification(self):
        error = urllib.error.HTTPError("https://invalid.example/private-url", 403, "private-message",
            {"x-amzn-errortype": "AccessDeniedException:private-header"},
            io.BytesIO(b'{"Message":"Authentication failed: Please make sure your API Key is valid. private-secret"}'))
        result, opener, _ = self.run_catalog(error,
            self.response({"inferenceProfileSummaries": [self.profile(self.global_id)]}))
        self.assertEqual(opener.open.call_count, 2)
        self.assertFalse(result["complete"])
        self.assertFalse(result["foundationModels"]["complete"])
        status = result["foundationModels"]["fetchStatus"]
        self.assertEqual(status["errorType"], "AccessDeniedException")
        self.assertEqual(status["outcome"], "server_rejected_authentication")
        self.assertTrue(result["inferenceProfiles"]["complete"])
        self.assertEqual(result["inferenceProfiles"]["profiles"], [{"id": self.global_id, "status": "ACTIVE"}])
        self.assertNotIn("private", json.dumps(result))
        self.assertFalse(result["accessVerified"])

    def test_catalog_failed_later_page_retains_previous_public_entries(self):
        error = urllib.error.HTTPError("https://invalid.example", 403, "private-message",
            {"x-amzn-errortype": "AccessDeniedException"}, io.BytesIO(b'{"Message":"Not authorized"}'))
        result, opener, _ = self.run_catalog(self.response({"modelSummaries": []}),
            self.response({"inferenceProfileSummaries": [self.profile(self.global_id)], "nextToken": "private-cursor"}), error)
        profiles = result["inferenceProfiles"]
        self.assertFalse(profiles["complete"])
        self.assertEqual(profiles["pagesFetched"], 1)
        self.assertEqual(profiles["profiles"], [{"id": self.global_id, "status": "ACTIVE"}])
        self.assertEqual(profiles["fetchStatus"]["outcome"], "access_denied_key_validity_unknown")
        self.assertEqual(opener.open.call_count, 3)
        self.assertNotIn("private", json.dumps(result))

    def test_catalog_terminal_cursor_completes_without_an_extra_request(self):
        for terminal in ({}, {"nextToken": None}, {"nextToken": ""}):
            for previous_page in (False, True):
                with self.subTest(terminal=terminal, previous_page=previous_page):
                    responses = [self.response({"modelSummaries": []})]
                    expected = [{"id": self.global_id, "status": "ACTIVE"}]
                    if previous_page:
                        responses.append(self.response({"inferenceProfileSummaries": [self.profile(self.apac_id)],
                                                        "nextToken": "private-continuation"}))
                        expected.insert(0, {"id": self.apac_id, "status": "ACTIVE"})
                    responses.append(self.response({"inferenceProfileSummaries": [self.profile(self.global_id)], **terminal}))
                    # A terminal page exactly at the limit is still complete.
                    page_count = 2 if previous_page else 1
                    with patch.object(diagnostic, "MAX_CATALOG_PAGES", page_count), \
                            patch.object(diagnostic, "MAX_CATALOG_ITEMS", page_count):
                        result, opener, _ = self.run_catalog(*responses)
                    profiles = result["inferenceProfiles"]
                    self.assertTrue(result["complete"])
                    self.assertTrue(profiles["complete"])
                    self.assertEqual(profiles["profiles"], expected)
                    self.assertEqual(profiles["pagesFetched"], page_count)
                    self.assertNotIn("incompleteReason", profiles)
                    self.assertEqual(opener.open.call_count, page_count + 1)
                    self.assertFalse(result["accessVerified"])
                    self.assertFalse(result["inferenceTested"])
                    self.assertNotIn("private-continuation", json.dumps(result))

    def test_catalog_invalid_cursor_is_incomplete_and_never_emitted_or_requested(self):
        for cursor in ("private cursor", "private\nsecret", "private\0secret", "x" * 2049,
                       chr(0xD800), False, 0, [], {}, 7, ["private-cursor"]):
            with self.subTest(cursor_type=type(cursor).__name__):
                result, opener, _ = self.run_catalog(self.response({"modelSummaries": []}),
                    self.response({"inferenceProfileSummaries": [self.profile(self.global_id)], "nextToken": cursor}))
                profiles = result["inferenceProfiles"]
                self.assertFalse(profiles["complete"])
                self.assertEqual(profiles["incompleteReason"], "invalid_pagination_cursor")
                self.assertEqual(opener.open.call_count, 2)
                self.assertNotIn("private", json.dumps(result))
                self.assertNotIn("x" * 2049, json.dumps(result))

    def test_catalog_repeated_cursor_and_page_limit_are_explicit_and_bounded(self):
        first = {"inferenceProfileSummaries": [self.profile(self.global_id)], "nextToken": "private-one"}
        second = {"inferenceProfileSummaries": [self.profile(self.apac_id)], "nextToken": "private-one"}
        result, opener, _ = self.run_catalog(self.response({"modelSummaries": []}),
                                            self.response(first), self.response(second))
        self.assertEqual(result["inferenceProfiles"]["incompleteReason"], "repeated_pagination_cursor")
        self.assertEqual(len(result["inferenceProfiles"]["profiles"]), 2)
        self.assertEqual(opener.open.call_count, 3)
        self.assertNotIn("private", json.dumps(result))
        with patch.object(diagnostic, "MAX_CATALOG_PAGES", 1):
            result, opener, _ = self.run_catalog(self.response({"modelSummaries": []}), self.response(first))
        self.assertEqual(result["inferenceProfiles"]["incompleteReason"], "pagination_page_limit")
        self.assertEqual(opener.open.call_count, 2)

    def test_catalog_response_and_entry_limits_report_incomplete(self):
        oversized = self.response(b"private-secret" * 100)
        with patch.object(diagnostic, "MAX_CATALOG_RESPONSE", 64):
            result, opener, _ = self.run_catalog(oversized, self.response({"inferenceProfileSummaries": []}))
        self.assertEqual(result["foundationModels"]["fetchStatus"]["outcome"], "response_size_limit")
        self.assertFalse(result["complete"])
        oversized.read.assert_called_once_with(65)
        self.assertNotIn("private-secret", json.dumps(result))
        with patch.object(diagnostic, "MAX_CATALOG_ITEMS", 1):
            result, _, _ = self.run_catalog(self.response({"modelSummaries": []}), self.response({
                "inferenceProfileSummaries": [self.profile(self.global_id), self.profile(self.apac_id)]}))
        self.assertEqual(result["inferenceProfiles"]["incompleteReason"], "catalog_item_limit")
        self.assertEqual(len(result["inferenceProfiles"]["profiles"]), 1)

    def test_catalog_invalid_success_bodies_and_redirects_never_verify_inference(self):
        for payload in (b"private-invalid-json", [], {"modelSummaries": "private-secret"}):
            with self.subTest(payload_type=type(payload).__name__):
                result, _, _ = self.run_catalog(self.response(payload), self.response({"inferenceProfileSummaries": []}))
                self.assertFalse(result["foundationModels"]["complete"])
                self.assertEqual(result["foundationModels"]["fetchStatus"]["outcome"], "invalid_catalog_response")
                self.assertNotIn("private", json.dumps(result))
        result, opener, _ = self.run_catalog(self.response(b"private-redirect", 302),
                                           self.response({"inferenceProfileSummaries": []}))
        self.assertEqual(result["foundationModels"]["fetchStatus"]["outcome"], "redirect_blocked")
        self.assertEqual(opener.open.call_count, 2)
        self.assertFalse(result["accessVerified"])

    def test_catalog_http_error_body_and_close_failure_preserve_partial_results(self):
        error = urllib.error.HTTPError("https://invalid.example/private", 403, "private-message",
            {"x-amzn-errortype": "AccessDeniedException:private-detail"}, io.BytesIO())
        with patch.object(error, "read", side_effect=TimeoutError("private-read-secret")), \
                patch.object(error, "close", side_effect=OSError("private-close-secret")):
            result, opener, _ = self.run_catalog(
                self.response({"modelSummaries": [{"modelId": self.base_id}]}),
                self.response({"inferenceProfileSummaries": [self.profile(self.global_id)], "nextToken": "private-cursor"}), error)
        profiles = result["inferenceProfiles"]
        self.assertFalse(result["complete"])
        self.assertTrue(result["foundationModels"]["complete"])
        self.assertEqual(profiles["profiles"], [{"id": self.global_id, "status": "ACTIVE"}])
        self.assertEqual(profiles["incompleteReason"], "response_body_read_failed")
        self.assertEqual(profiles["fetchStatus"]["httpStatus"], 403)
        self.assertEqual(profiles["fetchStatus"]["errorType"], "AccessDeniedException")
        self.assertEqual(profiles["fetchStatus"]["responseClose"], "failed")
        self.assertEqual(opener.open.call_count, 3)
        self.assertNotIn("private", json.dumps(result))

    def test_catalog_reuses_environment_and_header_guards(self):
        with patch.object(diagnostic.urllib.request, "build_opener") as factory:
            for override in ({"HTTPS_PROXY": "private-proxy"}, {"AWS_ENDPOINT_URL_BEDROCK": "private-url"},
                             {"CLAUDE_CODE_USE_MANTLE": "true"}):
                env = {**self.base, **override}
                report = diagnostic.build_report(env, env, "loaded", {}, "absent", do_list_models=True)
                self.assertTrue(report["network"]["outcome"].startswith("skipped_"))
                self.assertNotIn("private", json.dumps(report))
            for fresh, settings_status in ((None, "loaded"), (self.base, "unreadable_or_invalid")):
                report = diagnostic.build_report(self.base, fresh, "loaded", {}, settings_status, do_list_models=True)
                self.assertEqual(report["network"]["outcome"], "skipped_incomplete_environment_or_settings")
            for token, region in (("", "ap-northeast-2"), ("private\nsecret", "ap-northeast-2"),
                                  ("fixture", "private.example.com")):
                result = diagnostic.list_models(token, region)
                self.assertFalse(result["requestSent"])
                self.assertFalse(result["complete"])
                self.assertNotIn("private", json.dumps(result))
            diagnostic.build_report(self.base, self.base, "loaded", {}, "absent")
            report = diagnostic.build_report(self.base, self.base, "loaded", {}, "absent",
                                             do_inference=True, do_list_models=True)
            self.assertEqual(report["network"]["outcome"], "skipped_conflicting_network_modes")
            factory.assert_not_called()

    def test_catalog_cli_selects_only_catalog_and_default_cli_stays_offline(self):
        for argv in (["diagnose-bedrock.py"], ["diagnose-bedrock.py", "--list-models"]):
            with self.subTest(argv=argv), patch.object(diagnostic.sys, "argv", argv), \
                    patch.object(diagnostic.os, "environ", self.base), \
                    patch.object(diagnostic, "fresh_shell_environment", return_value=(self.base, "loaded")), \
                    patch.object(diagnostic, "read_user_settings", return_value=({}, "absent")), \
                    patch.object(diagnostic, "list_models", return_value={"complete": True, "accessVerified": False}) as catalog, \
                    patch.object(diagnostic, "check_network") as network, \
                    patch.object(diagnostic.sys, "stdout", io.StringIO()) as output:
                diagnostic.main()
                report = json.loads(output.getvalue())
                network.assert_not_called()
                if "--list-models" in argv:
                    catalog.assert_called_once_with("shell-fixture", "ap-northeast-2")
                    self.assertTrue(report["catalogRequested"])
                    self.assertNotIn("requestedModel", report)
                else:
                    catalog.assert_not_called()
                    self.assertFalse(report["networkRequested"])

    def test_catalog_cli_mode_is_mutually_exclusive_before_configuration_reads(self):
        for other in ("--check-network", "--check-inference"):
            with self.subTest(other=other), patch.object(diagnostic.sys, "argv", ["diagnose-bedrock.py", "--list-models", other]), \
                    patch.object(diagnostic, "fresh_shell_environment") as shell, \
                    patch.object(diagnostic, "read_user_settings") as settings, \
                    patch.object(diagnostic.sys, "stderr", io.StringIO()):
                with self.assertRaises(SystemExit) as failure:
                    diagnostic.main()
                self.assertEqual(failure.exception.code, 2)
                shell.assert_not_called()
                settings.assert_not_called()


if __name__ == "__main__":
    unittest.main()
