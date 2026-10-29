"""The review-blocker gate's rules, split by responsibility.

`gitio` runs git and parses the diff, `sql` reads D1's policy statements,
`rules` holds the added-line patterns, `ratchet` counts and gates U1, and `scan`
joins them for one change set. `scripts/check-review-blockers.py` is the entry
point.
"""
