# RedTail MA Cross identifier checks

Run from the repository root:

```sh
python3 -m unittest discover -s tests/RedTailMACross -p 'test_*.py'
```

These source-level checks guard against collisions with the original
`MACrossFilter` and duplicate generated factory declarations. They do not
compile NinjaScript or test NinjaTrader behavior. Compile in the NT8
NinjaScript Editor and verify signals and alerts there before use.
