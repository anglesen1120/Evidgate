"""scripts/plan_graph.py must report one graph for every PYTHONHASHSEED, and a story wave must be
the longest path over the condensation of the story graph (the "[a + b]" bundles of
plans/features/README.md §5.1) — the same number scripts/feature_index.py places the story at.

Stories whose tasks interleave depend on each other at story level, so the story graph is not a
DAG. Memoising the raw graph stored cycle-truncated values, so the wave of every story downstream
of a cycle moved with set iteration order (max wave 34 for most seeds, 37 for others; 23 is the
condensed answer and the one the README tables use). These tests run the real scripts in
subprocesses, one per hash seed, and rebuild the condensation independently.
"""
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SEEDS = (0, 1, 2, 3, 6, 7, 11, 42, 99, 123)


def run(script, *args, seed=None):
    env = dict(os.environ)
    if seed is None:
        env.pop("PYTHONHASHSEED", None)
    else:
        env["PYTHONHASHSEED"] = str(seed)
    done = subprocess.run([sys.executable, script, *args], cwd=ROOT, env=env,
                          capture_output=True, text=True)
    if done.returncode:
        raise AssertionError(f"{script} {' '.join(args)} exited {done.returncode}: {done.stderr}")
    return done.stdout


def stories(seed=None):
    return json.loads(run("scripts/plan_graph.py", "--json", seed=seed))["stories"]


def waves(seed=None):
    return {s: d["wave"] for s, d in stories(seed).items()}


def placed():
    """Story id -> wave, as plans/features/README.md §5.1 and §7 place it."""
    return {r["id"]: r["wave"] for r in json.loads(run("scripts/feature_index.py", "--json"))}


def report_line(prefix):
    return next(line for line in run("scripts/plan_graph.py").splitlines() if line.startswith(prefix))


class Determinism(unittest.TestCase):
    def test_report_is_byte_identical_for_every_hash_seed(self):
        outs = {seed: run("scripts/plan_graph.py", seed=seed) for seed in SEEDS}
        maxima = {seed: out.split("max wave: ")[1].split("\n")[0] for seed, out in outs.items()}
        self.assertEqual(len(set(outs.values())), 1,
                         f"output moved with PYTHONHASHSEED: {maxima}")

    def test_json_waves_are_identical_for_every_hash_seed(self):
        per_seed = {seed: waves(seed) for seed in SEEDS}
        base = per_seed[SEEDS[0]]
        for seed, got in per_seed.items():
            differing = {s: (base[s], got[s]) for s in base if base[s] != got[s]}
            self.assertEqual(differing, {}, f"story waves differ for PYTHONHASHSEED={seed}")

    def test_totals_do_not_move_with_the_seed(self):
        totals = {seed: (report_line("TOTAL "), report_line("by milestone:")) for seed in SEEDS}
        self.assertEqual(len(set(totals.values())), 1, totals)


class WaveSemantics(unittest.TestCase):
    def test_wave_is_the_longest_path_over_the_story_condensation(self):
        graph = stories()
        deps = {s: {d for d in graph[s]["deps"] if d in graph} for s in graph}
        # Condense the story graph (Tarjan) here, so the expectation does not reuse the script's own
        # grouping; every member of one component shares the component's longest-path wave.
        sys.setrecursionlimit(10000)
        index, low, stack, on, comp, counter = {}, {}, [], set(), {}, [0]

        def visit(v):
            index[v] = low[v] = counter[0]
            counter[0] += 1
            stack.append(v)
            on.add(v)
            for w in sorted(deps[v]):
                if w not in index:
                    visit(w)
                    low[v] = min(low[v], low[w])
                elif w in on:
                    low[v] = min(low[v], index[w])
            if low[v] == index[v]:
                members = []
                while True:
                    w = stack.pop()
                    on.discard(w)
                    members.append(w)
                    if w == v:
                        break
                for m in members:
                    comp[m] = min(members)

        for s in sorted(graph):
            if s not in index:
                visit(s)

        expected = {}
        # Component-level deps: a bundle's members share one wave, so a bundle's wave is one plus the
        # highest wave among the components any of its members depend on.
        comp_deps = {c: set() for c in set(comp.values())}
        for s in graph:
            for dep in deps[s]:
                if comp[dep] != comp[s]:
                    comp_deps[comp[s]].add(comp[dep])

        def longest(c):
            if c not in expected:
                expected[c] = 1 + max([longest(d) for d in sorted(comp_deps[c])] or [0])
            return expected[c]

        for c in sorted(comp_deps):
            longest(c)
        self.assertEqual({s: graph[s]["wave"] for s in graph},
                         {s: expected[comp[s]] for s in graph})

    def test_waves_agree_with_the_readme_placement_table(self):
        mine, theirs = waves(), placed()
        self.assertEqual(sorted(mine), sorted(theirs))
        differing = {s: (mine[s], theirs[s]) for s in mine if mine[s] != theirs[s]}
        self.assertEqual(differing, {}, "plan_graph wave (mine) vs feature_index wave (theirs)")

    def test_max_wave_is_the_highest_placed_wave(self):
        self.assertIn(f"max wave: {max(placed().values())}", report_line("stories: "))


class RegenInputs(unittest.TestCase):
    """regen_docs.py copies feature_index.py output into plans/features/README.md verbatim, so a tie
    broken by set order (the longest-chain pick in --critical) made the regenerated §5.2 flip between
    two equal-length chains from one run to the next."""

    def test_feature_index_outputs_are_identical_for_every_hash_seed(self):
        for mode in ("--order", "--critical", "--bundles", "--index", "--json"):
            outs = {run("scripts/feature_index.py", mode, seed=seed) for seed in SEEDS}
            self.assertEqual(len(outs), 1, f"feature_index.py {mode} moved with PYTHONHASHSEED")


if __name__ == "__main__":
    unittest.main()
