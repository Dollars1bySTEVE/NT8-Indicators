// Updated IQCandlesGPU.cs

// Fixes for Color4 immutability and Color constructor ambiguity errors on lines 742, 767, and 1158

// Example fix (actual lines may vary)

// Line 742
// Original: Color4 color = new Color4(...);
// Updated:
Color4 color = Color4.FromArgb(...);

// Line 767
// Original: Color colorValue = new Color(...);
// Updated:
Color colorValue = Color.FromArgb(...);

// Line 1158
// Original: Color color1188 = new Color(...);
// Updated:
Color color1188 = Color.FromArgb(...);