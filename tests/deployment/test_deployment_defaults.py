import json
import re
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


class DeploymentDefaultsTests(unittest.TestCase):
    def test_shell_scripts_are_checked_out_with_lf(self) -> None:
        attributes = read(".gitattributes")
        self.assertRegex(attributes, r"(?m)^\*\.sh\s+text\s+eol=lf$")

        tracked_scripts = subprocess.check_output(
            ["git", "ls-files", "-z", "*.sh"],
            cwd=ROOT,
        ).split(b"\0")
        self.assertTrue(tracked_scripts)
        for script in filter(None, tracked_scripts):
            content = (ROOT / script.decode()).read_bytes()
            self.assertNotIn(b"\r", content, script.decode())

    def test_postgres_bootstrap_does_not_seed_scim_credentials(self) -> None:
        bootstrap = read("docker/postgres-init.sh")
        self.assertNotRegex(
            bootstrap,
            r"(?i)\bINSERT\s+INTO\s+\S*scim_service_principal_credentials\b",
        )

    def test_scim_container_tests_require_explicit_connection_settings(self) -> None:
        tests = read("tests/NexusAuth.Host.IntegrationTests/ScimContainerApiTests.cs")
        for variable in (
            "NEXUSAUTH_TEST_SCIM_BASE_URL",
            "NEXUSAUTH_TEST_SCIM_READ_TOKEN",
            "NEXUSAUTH_TEST_SCIM_READ_WRITE_TOKEN",
        ):
            self.assertIn(variable, tests)
        self.assertIn("Skip =", tests)
        self.assertNotIn("http://localhost:5100/scim/v2/", tests)

    def test_smtp_defaults_off_and_compose_can_enable_it_explicitly(self) -> None:
        settings = json.loads(read("src/NexusAuth.Host/appsettings.json"))
        self.assertIs(settings["Smtp"]["Enabled"], False)

        compose = read("docker-compose.yml")
        self.assertRegex(
            compose,
            r"(?m)^\s+NEXUSAUTH_SMTP_ENABLED:\s+\$\{NEXUSAUTH_SMTP_ENABLED:-false\}$",
        )
        self.assertRegex(
            read("deploy/direct/sso.env.example"),
            r"(?m)^NEXUSAUTH_SMTP_ENABLED=false$",
        )

    def test_direct_deployment_uses_single_authority_and_overridable_logout(self) -> None:
        workbench_env = read("deploy/direct/workbench.env.example")
        self.assertEqual(
            re.findall(
                r"(?m)^NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY=(.+)$",
                workbench_env,
            ),
            ["https://sso.example.com"],
        )
        self.assertNotIn(
            "NEXUSAUTH_WORKBENCH_AUTH_BACKCHANNEL_AUTHORITY",
            workbench_env,
        )
        self.assertRegex(
            read("deploy/direct/sso.env.example"),
            r"(?m)^NEXUSAUTH_JWT_ISSUER=https://sso\.example\.com$",
        )

        compose = read("docker-compose.yml")
        self.assertRegex(
            compose,
            r"(?m)^\s+NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY:\s+"
            r"\$\{NEXUSAUTH_JWT_ISSUER:-http://localhost:5100\}$",
        )
        self.assertNotIn(
            "NEXUSAUTH_WORKBENCH_AUTH_BACKCHANNEL_AUTHORITY",
            compose,
        )
        self.assertRegex(
            compose,
            re.escape(
                "NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI: "
                "${NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI:-http://localhost:5560/}"
            ),
        )


if __name__ == "__main__":
    unittest.main()
