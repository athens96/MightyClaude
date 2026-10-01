#!/usr/bin/env python3
"""밖에서 STUN 바인딩 요청이 닿는지 확인한다.

용법: python3 stun-probe.py <host> <port>

응답이 오면 0, 안 오면 1. UDP가 막혔는지(Oracle 보안 목록·OS 방화벽) 확인하는
가장 작은 검사다. 비교용으로 공개 STUN 서버(stun.cloudflare.com:3478)에 같은
요청을 보내 이 컴퓨터의 UDP 송신 자체는 되는지도 함께 알려 준다.
"""
import os
import socket
import struct
import sys

MAGIC = 0x2112A442


def binding_request(host: str, port: int, tries: int = 3) -> str | None:
    """응답이 오면 XOR-MAPPED-ADDRESS 문자열, 없으면 None."""
    message = struct.pack("!HHI12s", 0x0001, 0, MAGIC, os.urandom(12))
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(3)
    try:
        addr = (socket.gethostbyname(host), port)
    except socket.gaierror:
        return None
    for _ in range(tries):
        sock.sendto(message, addr)
        try:
            data = sock.recvfrom(2048)[0]
        except socket.timeout:
            continue
        length = struct.unpack("!HHI12s", data[:20])[1]
        body, i = data[20 : 20 + length], 0
        while i + 4 <= len(body):
            atype, alen = struct.unpack("!HH", body[i : i + 4])
            value = body[i + 4 : i + 4 + alen]
            if atype in (0x0020, 0x8020) and len(value) >= 8:
                mapped_port = struct.unpack("!H", value[2:4])[0] ^ (MAGIC >> 16)
                mapped_ip = struct.unpack("!I", value[4:8])[0] ^ MAGIC
                return "%s:%d" % (socket.inet_ntoa(struct.pack("!I", mapped_ip)), mapped_port)
            i += 4 + alen + ((4 - alen % 4) % 4)
        return "(응답 있음, XOR-MAPPED-ADDRESS 없음)"
    return None


def main() -> int:
    host, port = sys.argv[1], int(sys.argv[2])
    mapped = binding_request(host, port)
    if mapped is not None:
        print("  ok   %s:%d에 STUN이 닿습니다 (내 주소 %s)" % (host, port, mapped))
        return 0
    control = binding_request("stun.cloudflare.com", 3478, tries=2)
    if control is None:
        print("  FAIL %s:%d 무응답 — 이 컴퓨터의 UDP 송신 자체가 막혀 있습니다" % (host, port))
    else:
        print(
            "  FAIL %s:%d 무응답 (이 컴퓨터의 UDP/3478 송신은 정상: 공개 STUN 응답 %s)"
            % (host, port, control)
        )
    return 1


if __name__ == "__main__":
    sys.exit(main())
