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

    def test_generated_factories_are_not_duplicated(self):
        marker = "#region NinjaScript generated code. Neither change nor remove."
        self.assertEqual(self.source.count(marker), 1)
        generated = self.source.split(marker, 1)[1]
        self.assertEqual(len(re.findall(r"\bcacheRedTailMACross\s*;", generated)), 1)
        for namespace in ("Indicators", "MarketAnalyzerColumns", "Strategies"):
            pattern = (
                r"namespace\s+NinjaTrader\.NinjaScript\."
                + namespace
                + r"\s*\{(.*?)(?=\nnamespace\s+NinjaTrader\.NinjaScript\.|\n#endregion)"
            )
            match = re.search(pattern, generated, re.DOTALL)
            self.assertIsNotNone(match, namespace)
            declarations = re.findall(
                r"public\s+(?:Indicators\.)?RedTailMACross\s+RedTailMACross"
                r"\(([^)]*)\)",
                match.group(1),
            )
            self.assertEqual(len(declarations), 2, namespace)
            self.assertEqual(sum("ISeries<double> input" in d for d in declarations), 1, namespace)
            for declaration in declarations:
                self.assertIn("RedTailMACrossMAType mAType", declaration)


if __name__ == "__main__":
    unittest.main()
