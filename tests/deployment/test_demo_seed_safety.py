import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


class DemoSeedSafetyTests(unittest.TestCase):
    def test_seed_scripts_require_explicit_isolated_demo_confirmation(self) -> None:
        for relative_path in ("demo/seed.sql", "demo/mobile-seed.sql"):
            with self.subTest(seed=relative_path):
                seed = read(relative_path)
                self.assertIn(
                    r"\if :{?NEXUSAUTH_ALLOW_DEMO_SEED}",
                    seed,
                )
                self.assertIn("WHEN :'NEXUSAUTH_ALLOW_DEMO_SEED' = '1'", seed)
                self.assertIn(
                    r"\if :{?NEXUSAUTH_DEMO_DATABASE}",
                    seed,
                )
                self.assertIn("AND :'NEXUSAUTH_DEMO_DATABASE' <> ''", seed)
                self.assertIn(
                    "END AS nexusauth_demo_seed_allowed",
                    seed,
                )
                self.assertIn(r"\gset", seed)
                self.assertIn(r"\if :nexusauth_demo_seed_allowed", seed)
                self.assertIn(r'\connect :"NEXUSAUTH_DEMO_DATABASE"', seed)
                self.assertEqual(seed.count(r"\quit"), 1)
                self.assertIn("ISOLATED DEMO DATABASES ONLY", seed)
                self.assertNotRegex(seed, r"(?i)\bDO\s+UPDATE\b")

    def test_full_seed_never_updates_existing_users(self) -> None:
        seed = read("demo/seed.sql")
        user_insert = re.search(
            r"INSERT\s+INTO\s+users\b.*?;\s*",
            seed,
            flags=re.IGNORECASE | re.DOTALL,
        )
        self.assertIsNotNone(user_insert)
        assert user_insert is not None
        statement = user_insert.group(0)
        self.assertRegex(statement, r"(?i)ON\s+CONFLICT\s+DO\s+NOTHING")
        self.assertNotRegex(statement, r"(?i)\bDO\s+UPDATE\b")
        self.assertNotIn("is_system_account", statement)
        self.assertNotIn("password_hash = EXCLUDED.password_hash", statement)
        self.assertNotIn("email = EXCLUDED.email", statement)
        self.assertNotIn("is_active = EXCLUDED.is_active", statement)
        self.assertNotRegex(seed, r"(?is)\bUPDATE\s+users\b")
        self.assertNotRegex(
            seed,
            r"(?is)UPDATE\s+users\s+SET\s+is_system_account",
        )

    def test_seed_scripts_use_insert_only_conflict_handling(self) -> None:
        for relative_path in ("demo/seed.sql", "demo/mobile-seed.sql"):
            with self.subTest(seed=relative_path):
                seed = read(relative_path)
                self.assertNotRegex(seed, r"(?i)\bON\s+CONFLICT\b[^;]*\bDO\s+UPDATE\b")
                self.assertNotRegex(seed, r"(?i)\bUPDATE\s+oauth_client_secrets\b")

    def test_documentation_uses_confirmation_and_warns_about_public_material(self) -> None:
        docs = (
            read("demo/README.md")
            + "\n"
            + read("demo/src/Demo.Mobile.Backend/README.md")
        )
        self.assertIn("NEXUSAUTH_ALLOW_DEMO_SEED=1", docs)
        self.assertIn("NEXUSAUTH_DEMO_DATABASE=nexusauth_demo", docs)
        self.assertRegex(docs, r"(?i)private keys committed")
        self.assertRegex(docs, r"(?i)public shared development secret")
        self.assertNotRegex(
            read("demo/src/Demo.Mobile.Backend/README.md"),
            r"(?im)^.*admin\s*/\s*\S+",
        )


if __name__ == "__main__":
    unittest.main()
