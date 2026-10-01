#!/usr/bin/env python3
"""TURN Allocate 확인기.

용법: echo -n "<username>\t<password>" | python3 turn-allocate-check.py <host> <port> [min_port] [max_port]
      python3 turn-allocate-check.py --no-credentials <host> <port>

릴레이가 발급한 단기 HMAC 자격증명으로 coturn에 Allocate를 요청한다. 성공하면
(1) 릴레이의 use-auth-secret과 coturn의 static-auth-secret이 같은 값이고,
(2) coturn이 자격증명을 받아들이며,
(3) 중계 포트가 설정한 범위 안에 있다는 것이 한 번에 증명된다.
자격증명은 argv가 아니라 stdin으로 받고(프로세스 목록에 남지 않게) 화면에 찍지 않는다.

--no-credentials는 반대를 확인한다: 자격증명 없는 Allocate가 401로 거절되어야
한다. coturn이 설정 파일을 읽지 못하면 경고 한 줄만 남기고 기본값(인증 없는
공개 중계)으로 뜨기 때문에, 배포 뒤 이 검사를 반드시 통과해야 한다.
"""
import hashlib
import hmac
import os
import socket
import struct
import sys

MAGIC = 0x2112A442
ALLOCATE, SUCCESS, ERROR = 0x0003, 0x0103, 0x0113
A_USERNAME, A_MSG_INTEGRITY, A_ERROR, A_REALM, A_NONCE = 0x0006, 0x0008, 0x0009, 0x0014, 0x0015
A_XOR_RELAYED, A_REQUESTED_TRANSPORT, A_LIFETIME = 0x0016, 0x0019, 0x000D


def pad(value: bytes) -> bytes:
    return value + b"\x00" * ((4 - len(value) % 4) % 4)


def attr(kind: int, value: bytes) -> bytes:
    return struct.pack("!HH", kind, len(value)) + pad(value)


def message(kind: int, tid: bytes, attrs: bytes, key: bytes | None = None) -> bytes:
    if key is None:
        return struct.pack("!HHI12s", kind, len(attrs), MAGIC, tid) + attrs
    # MESSAGE-INTEGRITY: 길이 필드는 자기 자신(24바이트)까지 포함해서 계산한다.
    head = struct.pack("!HHI12s", kind, len(attrs) + 24, MAGIC, tid)
    digest = hmac.new(key, head + attrs, hashlib.sha1).digest()
    return head + attrs + attr(A_MSG_INTEGRITY, digest)


def parse(data: bytes) -> tuple[int, dict[int, bytes]]:
    kind, length, _magic, _tid = struct.unpack("!HHI12s", data[:20])
    body, out, i = data[20 : 20 + length], {}, 0
    while i + 4 <= len(body):
        atype, alen = struct.unpack("!HH", body[i : i + 4])
        out[atype] = body[i + 4 : i + 4 + alen]
        i += 4 + alen + ((4 - alen % 4) % 4)
    return kind, out


def xor_address(value: bytes) -> str:
    port = struct.unpack("!H", value[2:4])[0] ^ (MAGIC >> 16)
    ip = struct.unpack("!I", value[4:8])[0] ^ MAGIC
    return "%s:%d" % (socket.inet_ntoa(struct.pack("!I", ip)), port)


def unauthenticated_allocate(host: str, port: int) -> tuple[int, dict[int, bytes]] | None:
    """자격증명 없는 Allocate를 보내고 응답을 돌려준다(없으면 None)."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(5)
    addr = (socket.gethostbyname(host), port)
    request = message(
        ALLOCATE, os.urandom(12), attr(A_REQUESTED_TRANSPORT, struct.pack("!BBBB", 17, 0, 0, 0))
    )
    for _ in range(3):
        sock.sendto(request, addr)
        try:
            return parse(sock.recvfrom(2048)[0])
        except socket.timeout:
            continue
    return None


def check_auth_required(host: str, port: int) -> int:
    """coturn이 인증을 요구하는지(= 설정 파일을 읽었는지) 확인한다."""
    answer = unauthenticated_allocate(host, port)
    if answer is None:
        print("  FAIL coturn이 %s:%d에서 Allocate에 응답하지 않습니다" % (host, port))
        return 1
    kind, attrs = answer
    if kind == SUCCESS:
        print("  FAIL coturn이 자격증명 없이 중계를 내줬습니다 — 설정 파일을 못 읽은 공개 중계 상태입니다")
        return 1
    if kind != ERROR or A_REALM not in attrs or A_NONCE not in attrs:
        print("  FAIL 자격증명 없는 Allocate에 401(REALM·NONCE)이 오지 않았습니다 (type=0x%04x)" % kind)
        return 1
    print("  ok   coturn이 인증을 요구합니다 (401, realm=%s)" % attrs[A_REALM].decode("utf-8", "replace"))
    return 0


def main() -> int:
    if sys.argv[1] == "--no-credentials":
        return check_auth_required(sys.argv[2], int(sys.argv[3]))
    host, port = sys.argv[1], int(sys.argv[2])
    min_port = int(sys.argv[3]) if len(sys.argv) > 3 else 49160
    max_port = int(sys.argv[4]) if len(sys.argv) > 4 else 49200
    raw = sys.stdin.read().strip()
    if "\t" not in raw:
        print("  FAIL stdin에 '<username>\\t<password>'가 필요합니다")
        return 2
    username, password = raw.split("\t", 1)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(5)
    addr = (socket.gethostbyname(host), port)

    # 1차: 자격증명 없이 → 401 + REALM/NONCE
    tid = os.urandom(12)
    request = message(ALLOCATE, tid, attr(A_REQUESTED_TRANSPORT, struct.pack("!BBBB", 17, 0, 0, 0)))
    sock.sendto(request, addr)
    try:
        kind, attrs = parse(sock.recvfrom(2048)[0])
    except socket.timeout:
        print("  FAIL coturn이 Allocate에 응답하지 않습니다 (%s:%d)" % addr)
        return 1
    if kind != ERROR or A_REALM not in attrs or A_NONCE not in attrs:
        print("  FAIL 1차 Allocate에 401(REALM·NONCE)이 오지 않았습니다 (type=0x%04x)" % kind)
        return 1
    realm, nonce = attrs[A_REALM], attrs[A_NONCE]

    # 2차: long-term 자격증명으로 서명
    key = hashlib.md5(b"%s:%s:%s" % (username.encode(), realm, password.encode())).digest()
    tid = os.urandom(12)
    attrs_out = (
        attr(A_REQUESTED_TRANSPORT, struct.pack("!BBBB", 17, 0, 0, 0))
        + attr(A_LIFETIME, struct.pack("!I", 600))
        + attr(A_USERNAME, username.encode())
        + attr(A_REALM, realm)
        + attr(A_NONCE, nonce)
    )
    sock.sendto(message(ALLOCATE, tid, attrs_out, key), addr)
    try:
        kind, attrs = parse(sock.recvfrom(2048)[0])
    except socket.timeout:
        print("  FAIL 서명한 Allocate에 응답이 없습니다")
        return 1
    if kind != SUCCESS:
        code = attrs.get(A_ERROR, b"\x00\x00\x00\x00")
        number = code[2] * 100 + code[3] if len(code) >= 4 else 0
        print("  FAIL coturn이 Allocate를 거부했습니다 (오류 %d %s)" % (number, code[4:].decode("utf-8", "replace")))
        return 1
    relayed = xor_address(attrs[A_XOR_RELAYED])
    relay_port = int(relayed.rsplit(":", 1)[1])
    realm_text = realm.decode("utf-8", "replace")
    if not min_port <= relay_port <= max_port:
        print("  FAIL 중계 포트 %d가 범위 %d-%d 밖입니다" % (relay_port, min_port, max_port))
        return 1
    print(
        "  ok   Allocate 성공: realm=%s 중계주소=%s (범위 %d-%d 안)"
        % (realm_text, relayed, min_port, max_port)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
