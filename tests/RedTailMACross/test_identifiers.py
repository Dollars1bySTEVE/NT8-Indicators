import re
import unittest
from pathlib import Path


SOURCE = Path(__file__).resolve().parents[2] / "Indicators" / "RedTailMACross.cs"


class IndicatorIdentifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_original_indicator_identifiers_are_not_redeclared(self):
        self.assertNotRegex(
            self.source,
            r"\b(?:MACrossFilterMAType|MACrossFilter|cacheMACrossFilter)\b",
        )

    def test_indicator_and_enum_have_unique_names(self):
        self.assertEqual(len(re.findall(r"\bclass\s+RedTailMACross\s*:", self.source)), 1)
        self.assertEqual(len(re.findall(r"\benum\s+RedTailMACrossMAType\b", self.source)), 1)

    def test_distribution_source_does_not_bundle_generated_factories(self):
        self.assertNotIn("#region NinjaScript generated code.", self.source)
        self.assertNotRegex(self.source, r"\bcacheRedTailMACross\b")
        self.assertNotRegex(
            self.source,
            r"\bRedTailMACross\s+RedTailMACross\s*\(",
        )


if __name__ == "__main__":
    unittest.main()
