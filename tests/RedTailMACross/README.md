# RedTail MA Cross identifier checks

Run from the repository root:

```sh
python3 -m unittest discover -s tests/RedTailMACross -p 'test_*.py'
```

These source-level checks guard against collisions with the original
`MACrossFilter` and ensure the distributed source does not bundle factory
code that NinjaTrader may generate again. Replace the entire existing
`RedTailMACross.cs` with the distribution file rather than appending it to
an existing indicator. NinjaTrader can generate its own factory code.

These checks do not compile NinjaScript or test NinjaTrader behavior.
Compile in the NT8 NinjaScript Editor and verify signals and alerts there
before use.
