import json
import re
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


def read_json(relative_path: str) -> dict:
    return json.loads(read(relative_path))


def compose_service(compose: str, service: str) -> str:
    lines = compose.splitlines()
    marker = f"  {service}:"
    try:
        start = lines.index(marker)
    except ValueError as error:
        raise AssertionError(f"Compose service {service!r} was not found") from error

    block = []
    for line in lines[start + 1 :]:
        if re.match(r"^  [A-Za-z0-9_.-]+:\s*$", line) or (
            line and not line.startswith(" ")
        ):
            break
        block.append(line)
    return "\n".join(block)


def find_key_values(value, key: str) -> list:
    matches = []
    if isinstance(value, dict):
        for current_key, current_value in value.items():
            if current_key == key:
                matches.append(current_value)
            matches.extend(find_key_values(current_value, key))
    elif isinstance(value, list):
        for item in value:
            matches.extend(find_key_values(item, key))
    return matches


class DeploymentDefaultsTests(unittest.TestCase):
    def test_workbench_base_appsettings_keep_safe_defaults(self) -> None:
        settings = read_json("admin/src/NexusAuth.Workbench.Api/appsettings.json")
        development = read_json(
            "admin/src/NexusAuth.Workbench.Api/appsettings.Development.json"
        )

        self.assertIsNone(settings["Swagger"]["Enabled"])
        self.assertEqual(settings["ConnectionStrings"]["Default"], "")
        self.assertEqual(settings["Bootstrap"]["ResourceName"], "nexusauth.workbench.api")
        self.assertEqual(
            settings["Bootstrap"]["ResourceDisplayName"],
            "NexusAuth Workbench API",
        )
        self.assertIn("Redis", settings["Auth"]["FlowState"])
        self.assertEqual(development["Auth"]["ClientSecret"], "")

    def test_workbench_production_json_and_default_mount_are_secure(self) -> None:
        settings = read_json("docker/workbench.appsettings.json")

        self.assertEqual(settings["Auth"]["Authority"], "")
        self.assertEqual(settings["Auth"]["ClientSecret"], "")
        self.assertIs(settings["Auth"]["RequireHttpsMetadata"], True)
        self.assertEqual(settings["Security"]["CookieSecurePolicy"], "Always")
        self.assertIs(settings["Swagger"]["Enabled"], False)
        self.assertIn(
            "REPLACE_WITH_DATABASE_PASSWORD",
            settings["ConnectionStrings"]["Default"],
        )

        admin_api = compose_service(read("docker-compose.yml"), "admin-api")
        self.assertIn(
            "${NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE:-./docker/workbench.appsettings.json}"
            ":/app/appsettings.${ASPNETCORE_ENVIRONMENT:-Production}.json:ro",
            admin_api,
        )
        self.assertNotRegex(
            admin_api,
            r"NEXUSAUTH_WORKBENCH_(?:AUTH|BOOTSTRAP|CONNECTION_STRINGS|SECURITY|SWAGGER)",
        )

    def test_local_compose_selects_local_http_configuration(self) -> None:
        settings = read_json("docker/workbench.local.appsettings.json")

        self.assertEqual(settings["Auth"]["Authority"], "http://localhost:5100")
        self.assertEqual(
            settings["Auth"]["RedirectUri"],
            "http://localhost:5560/signin-oidc",
        )
        self.assertEqual(
            settings["Auth"]["PostLogoutRedirectUri"],
            "http://localhost:5560/",
        )
        self.assertIs(settings["Auth"]["RequireHttpsMetadata"], False)
        self.assertEqual(settings["Security"]["CookieSecurePolicy"], "SameAsRequest")
        self.assertIn("nexusauth_dev_password", settings["ConnectionStrings"]["Default"])

        admin_api = compose_service(read("docker-compose.local.yml"), "admin-api")
        self.assertIn(
            "${NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE:-./docker/workbench.local.appsettings.json}"
            ":/app/appsettings.${ASPNETCORE_ENVIRONMENT:-Production}.json:ro",
            admin_api,
        )

    def test_direct_workbench_uses_single_authority_json_and_callbacks(self) -> None:
        workbench = read_json("deploy/direct/workbench.appsettings.example.json")
        sso = read_json("deploy/direct/sso.appsettings.example.json")

        self.assertEqual(
            find_key_values(workbench, "Authority"),
            ["https://sso.example.com"],
        )
        self.assertEqual(sso["Jwt"]["Issuer"], "https://sso.example.com")
        self.assertEqual(workbench["Auth"]["ClientSecret"], "")
        self.assertEqual(
            workbench["Auth"]["RedirectUri"],
            "https://workbench.example.com/signin-oidc",
        )
        self.assertEqual(
            workbench["Auth"]["PostLogoutRedirectUri"],
            "https://workbench.example.com/",
        )

        admin_api = compose_service(read("deploy/direct/compose.yml"), "admin-api")
        self.assertIn(
            "./workbench.appsettings.json:/app/appsettings.Production.json:ro",
            admin_api,
        )
        self.assertIn("path: ./workbench.env", admin_api)
        environment_entries = [
            line.strip()
            for line in read("deploy/direct/workbench.env.example").splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        ]
        self.assertTrue(environment_entries)
        self.assertTrue(
            all(entry.startswith("ASPNETCORE_") for entry in environment_entries),
            environment_entries,
        )

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

    def test_host_smtp_defaults_off_and_uses_appsettings(self) -> None:
        settings = read_json("src/NexusAuth.Host/appsettings.json")
        self.assertIs(settings["Smtp"]["Enabled"], False)

        compose = read("docker-compose.yml")
        self.assertNotIn("NEXUSAUTH_SMTP_", compose)
        self.assertNotIn("NEXUSAUTH_SMTP_", read("deploy/direct/sso.env.example"))


if __name__ == "__main__":
    unittest.main()
