#!/usr/bin/env node
// Per-class test timing summary for the gate's test stages.
//
//   node scripts/class-timing.mjs [--top N] [--out FILE] <JUnit file or directory>...
//
// Reads the JUnit reports a test run left behind (`dotnet test --report-xunit-junit`
// and the isolation console runner's `-result-junit`), sums `<testcase time>` per
// `classname`, prints the top N classes (default 20) as `class  tests  seconds`
// and writes the full table, slowest first, to FILE as TSV. It is a reporting aid
// only: whatever is missing or unreadable becomes a warning on stderr and the exit
// code stays 0, so a stage's verdict never depends on it. Only a bad command line
// exits 2.
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const TESTCASE_TAG = /<testcase\b([^>]*?)\/?>/g;
const ATTRIBUTE = /([A-Za-z_][\w-]*)="([^"]*)"/g;

function parseArgs(argv) {
  const args = { top: 20, out: null, inputs: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--top' || a === '--out') {
      const v = argv[++i];
      if (v === undefined) throw new Error(`${a} needs a value`);
      if (a === '--out') args.out = v;
      else if (/^[1-9][0-9]{0,3}$/.test(v)) args.top = Number(v);
      else throw new Error(`--top must be a positive integer, got '${v}'`);
    } else if (a.startsWith('--')) {
      throw new Error(`unknown option ${a}`);
    } else {
      args.inputs.push(a);
    }
  }
  if (args.inputs.length === 0) throw new Error('no report file or directory given');
  return args;
}

function reportFiles(input, warn) {
  let stat;
  try {
    stat = statSync(input);
  } catch {
    warn(`${input}: not found`);
    return [];
  }
  if (!stat.isDirectory()) return [input];
  const files = readdirSync(input)
    .filter((n) => n.endsWith('.junit.xml'))
    .sort()
    .map((n) => join(input, n));
  if (files.length === 0) warn(`${input}: no *.junit.xml report`);
  return files;
}

/** Adds the testcases of one report to `classes`; returns the number it could not read. */
export function addReport(xml, classes) {
  let unreadable = 0;
  for (const tag of xml.matchAll(TESTCASE_TAG)) {
    const attrs = {};
    for (const m of tag[1].matchAll(ATTRIBUTE)) attrs[m[1]] = m[2];
    const seconds = Number(attrs.time);
    if (!attrs.classname || !Number.isFinite(seconds) || seconds < 0) {
      unreadable++;
      continue;
    }
    const entry = classes.get(attrs.classname) ?? { tests: 0, seconds: 0 };
    entry.tests++;
    entry.seconds += seconds;
    classes.set(attrs.classname, entry);
  }
  return unreadable;
}

export function summarize(inputs, warn = (m) => console.error(`class-timing: ${m}`)) {
  const classes = new Map();
  for (const input of inputs) {
    for (const file of reportFiles(input, warn)) {
      let xml;
      try {
        xml = readFileSync(file, 'utf8');
      } catch (e) {
        warn(`${file}: ${e.message}`);
        continue;
      }
      const before = classes.size;
      const bad = addReport(xml, classes);
      if (bad > 0) warn(`${file}: ${bad} testcase(s) without a usable classname/time were skipped`);
      if (classes.size === before && bad === 0 && !xml.includes('<testcase'))
        warn(`${file}: no testcase found`);
    }
  }
  return [...classes]
    .map(([name, v]) => ({ name, tests: v.tests, seconds: v.seconds }))
    .sort((a, b) => b.seconds - a.seconds || a.name.localeCompare(b.name));
}

function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(`class-timing: ${e.message}`);
    process.exit(2);
  }
  const rows = summarize(args.inputs);
  if (rows.length === 0) {
    console.error('class-timing: no test timing found; nothing to summarize');
    return;
  }
  const total = rows.reduce((s, r) => s + r.seconds, 0);
  const tests = rows.reduce((s, r) => s + r.tests, 0);
  console.log(
    `   slowest test classes by summed test duration (top ${Math.min(args.top, rows.length)} of ${rows.length}; ${tests} tests, ${total.toFixed(1)} s in total)`,
  );
  console.log(`   ${'seconds'.padStart(9)} ${'tests'.padStart(6)}  class`);
  for (const r of rows.slice(0, args.top)) {
    console.log(`   ${r.seconds.toFixed(2).padStart(9)} ${String(r.tests).padStart(6)}  ${r.name}`);
  }
  if (args.out) {
    try {
      mkdirSync(dirname(args.out), { recursive: true });
      const body = [
        'class\ttests\tseconds',
        ...rows.map((r) => `${r.name}\t${r.tests}\t${r.seconds.toFixed(3)}`),
      ];
      writeFileSync(args.out, `${body.join('\n')}\n`);
      console.log(`   full table: ${args.out}`);
    } catch (e) {
      console.error(`class-timing: cannot write ${args.out}: ${e.message}`);
    }
  }
}

import { fileURLToPath } from 'node:url';
if (process.argv[1] === fileURLToPath(import.meta.url)) main();
