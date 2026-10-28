# Formula Help

## Variables

- `u` = value of the previous item (first item uses the base value)
- `n` = current sequence number (starting from 1)
- Recursion uses exact values: each next item starts from the raw computed result of the previous one (same as Excel); round inside the formula if you need integers

## Operators

- `+ - * /`: arithmetic
- `%`: modulo (cycles / grouping)
- `**` or `^`: power (equivalent), e.g. `u**2`
- `>` `<` `>=` `<=` `==` `!=`: comparison (use with if)
- `( )`: parentheses
- `-`: unary minus (wrap negative exponents in parentheses, e.g. `u**(-2)`)

## Available functions (case-insensitive, lowercase recommended)

- `abs(x)`: absolute value. Example: `abs(u - 5)`
- `floor(x)`: round down. Example: `floor(7 / 4)` = 1
- `ceiling(x)`: round up. Example: `ceiling(1.2)` = 2
- `truncate(x)`: truncate toward zero. Example: `truncate(-1.5)` = -1
- `round(x)`: round to nearest integer. Example: `round(u * 1.06)`
- `round(x, d)`: round to d decimal places. Example: `round(1.65, 1)` = 1.7
- `min(a, b)`: smaller value (cap). Example: `min(u * 1.06, 100)`
- `max(a, b)`: larger value (floor). Example: `max(u - 1, 0)`
- `if(cond, trueVal, falseVal)`: two-way branch. Example: `if(n <= 10, u + 1, u + 5)`
- `ifs(cond1, val1, cond2, val2, ..., default)`: multi-way branch. Example: `ifs(n <= 4, 1, n <= 8, 2, 3)`
- `sqrt(x)`: square root. Example: `u + sqrt(n)`
- `exp(x)`: e to the power of x
- `log(x, base)`: logarithm. Example: `log(100, 10)` = 2
- `pow(x, y)`: x to the power of y (same as `**`)

## Common recipes

- Add a fixed amount (+5 per item): `u + 5`
- Subtract a fixed amount (-5 per item): `u - 5`
- Grow by ratio (x1.06 per item): `u * 1.06`
- Decay by ratio (x0.9 per item): `u * 0.9`
- Step up every N (+1 every 4 items): `1 + floor((n - 1) / 4)` → 1,1,1,1,2,2,2,2...
- Increasing with cap (+5 per item, capped at 100): `min(u + 5, 100)`
- Decreasing with floor (-1 per item, never below 0): `max(u - 1, 0)`
- Condition (+1 for the first 10 items, then +5): `if(n <= 10, u + 1, u + 5)`
- Add squares (+n^2 per item): `u + n**2`
- Cycle 1-5 with no zero: `(n - 1) % 5 + 1`
- Clamp result (+2 per item, kept within 10~100): `min(max(u + 2, 10), 100)`
- Explosive growth (x1.5 per item from 100, by index): `100 * 1.5 ^ (n - 1)`

## Important notes

- **For integer compound growth use the closed form** (compute item n directly): `round(1 * 1.06 ^ (n - 1))`.
  Do NOT write `round(u * 1.06)` — under exact recursion it gets stuck at the start (1 → 1.06 → round 1 → ...).
- Zero-padding applies to the **numeric segment** and runs before interpolation: segment `1` becomes `01`, then interpolation makes it `01a`.
- Rows with no real solution (division by zero, negative fractional powers) are marked with ! and skipped; invalid formulas show "formula invalid".
- All results keep at most 6 decimal places.
