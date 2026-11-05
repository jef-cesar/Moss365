#!/usr/bin/env python3
"""Refuses personal or organisation data in the repository.

Checks every tracked text file for:
- e-mail addresses outside the example domains;
- public IPv4 addresses outside the documentation and private ranges;
- terms from the PRIVACY_BLOCKLIST environment variable (a repository secret, one term per line). Matches are reported
  with file and line only, never with the term itself, so the secret stays secret.

Exceptions: one per line in .github/privacy-allowlist.txt, as "path:line-content-substring" or a plain substring.
"""
import ipaddress
import os
import re
import subprocess
import sys

ALLOWED_DOMAINS = {
    "contoso.com", "fabrikam.com", "example.org", "example.com", "example.net", "test.internal",
    "contoso.onmicrosoft.com", "test.onmicrosoft.com", "test.be", "anthropic.com", "users.noreply.github.com",
}
ALLOWED_NETS = [ipaddress.ip_network(n) for n in (
    "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16",
    "192.0.2.0/24", "198.51.100.0/24", "203.0.113.0/24", "255.255.255.255/32")]
SKIP_DIRS = ("lib/monaco/", "/bin/", "/obj/")
SKIP_EXT = (".png", ".jpg", ".ico", ".gif", ".dll", ".exe", ".zip", ".woff", ".woff2", ".ttf", ".db")

EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")
IPV4 = re.compile(r"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])")


def allowed_domain(domain: str) -> bool:
    d = domain.lower().rstrip(".")
    return any(d == a or d.endswith("." + a) for a in ALLOWED_DOMAINS)


def allowed_ip(text: str, line: str) -> bool:
    try:
        ip = ipaddress.ip_address(text)
    except ValueError:
        return True  # not an IP (e.g. 999.1.1.1)
    if "version" in line.lower():
        return True  # version numbers such as 4.0.0.0
    return any(ip in n for n in ALLOWED_NETS)


def main() -> int:
    files = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True).stdout.splitlines()
    allow = []
    if os.path.exists(".github/privacy-allowlist.txt"):
        allow = [a.strip() for a in open(".github/privacy-allowlist.txt", encoding="utf-8") if a.strip() and not a.startswith("#")]
    blocked = [t.strip().lower() for t in os.environ.get("PRIVACY_BLOCKLIST", "").splitlines() if t.strip()]

    problems = []
    for path in files:
        if any(s in "/" + path for s in SKIP_DIRS) or path.lower().endswith(SKIP_EXT) or path.startswith(".github/scripts/privacy_check"):
            continue
        try:
            lines = open(path, encoding="utf-8").read().splitlines()
        except (UnicodeDecodeError, FileNotFoundError):
            continue
        for number, line in enumerate(lines, 1):
            if any((a.split(":", 1)[0] == path and a.split(":", 1)[1] in line) if ":" in a and a.split(":", 1)[0] == path else a in line for a in allow):
                continue
            for m in EMAIL.finditer(line):
                if not allowed_domain(m.group(1)):
                    problems.append(f"{path}:{number}: e-mail address outside the example domains ({m.group(1)})")
            for m in IPV4.finditer(line):
                if not allowed_ip(m.group(1), line):
                    problems.append(f"{path}:{number}: public IP address {m.group(1)}")
            low = line.lower()
            if any(t in low for t in blocked):
                problems.append(f"{path}:{number}: contains a blocked term (see the PRIVACY_BLOCKLIST secret)")

    for p in problems:
        print(f"::error::{p}")
    print(f"Privacy check: {len(files)} files, {len(problems)} problem(s), {len(blocked)} blocked term(s) configured.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
