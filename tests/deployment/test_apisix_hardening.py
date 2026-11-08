import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


def service_block(compose: str, service: str) -> str:
    lines = compose.splitlines()
    marker = f"  {service}:"
    start = lines.index(marker)
    block = []
    for line in lines[start + 1 :]:
        if re.match(r"^  [A-Za-z0-9_.-]+:\s*$", line) or (
            line and not line.startswith(" ")
        ):
            break
        block.append(line)
    return "\n".join(block)


class ApisixHardeningTests(unittest.TestCase):
    def test_etcd_isolated_and_apisix_has_both_networks(self) -> None:
        compose = read("docker-compose.apisix.yml")
        apisix = service_block(compose, "apisix")
        etcd = service_block(compose, "apisix-etcd")

        self.assertRegex(apisix, r"(?m)^\s+nexusauth-gateway:\s*$")
        self.assertRegex(apisix, r"(?m)^\s+apisix-internal:\s*$")
        self.assertRegex(etcd, r"(?m)^\s+apisix-internal:\s*$")
        self.assertNotIn("nexusauth-gateway:", etcd)
        self.assertNotIn("ports:", etcd)
        self.assertIn("internal: true", compose)

    def test_field_encryption_requires_external_key(self) -> None:
        config = read("docker/apisix/config.yaml")
        compose = read("docker-compose.apisix.yml")
        start = read("docker/apisix/start-local.sh")

        self.assertIn("enable_encrypt_fields: true", config)
        self.assertIn('${{APISIX_DATA_ENCRYPTION_KEY}}', config)
        self.assertRegex(
            compose,
            r"(?m)^\s+APISIX_DATA_ENCRYPTION_KEY:\s+\$\{APISIX_DATA_ENCRYPTION_KEY:\?",
        )
        self.assertRegex(start, r"APISIX_DATA_ENCRYPTION_KEY")
        self.assertRegex(start, r"[0-9A-Fa-f].*16")

    def test_legacy_dashboard_with_plaintext_credentials_is_not_deployed(self) -> None:
        compose = read("docker-compose.apisix.yml")
        self.assertNotRegex(compose, r"(?m)^\s+apisix-dashboard:\s*$")
        self.assertFalse((ROOT / "docker/apisix/dashboard.yaml").exists())
        self.assertNotRegex(
            "\n".join(
                path.read_text(encoding="utf-8")
                for path in (ROOT / "docker/apisix").glob("*")
                if path.is_file()
            ),
            r"(?im)^\s*(?:username|password):\s*admin\s*$|development-secret",
        )


if __name__ == "__main__":
    unittest.main()
