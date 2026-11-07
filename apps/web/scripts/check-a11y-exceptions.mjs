#!/usr/bin/env node
// The schema check for `e2e/a11y-exceptions.json`: an accepted violation is data, so it can expire
// and cannot disable a rule. A missing field, an unknown key, a date that is not a day, a passed or
// too-distant review-by and a rule id axe-core does not know each fail the gate, one line each.
//
// `validate` is pure and exported, so the proofs in `check-a11y-exceptions.test.mjs` call it with a
// fixed `today` instead of depending on the clock. Run by `npm test` in this workspace, which the
// repository gate runs.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import axe from 'axe-core';

/** Every entry must carry these five fields; nothing else is accepted on an entry. */
const ENTRY_KEYS = ['rule', 'selector', 'reason', 'owner', 'review-by'];
/** The document may hold the schema pointer and the list, and nothing else. */
const DOCUMENT_KEYS = ['$schema', 'exceptions'];
/** An entry may not be dated further out than this: the list is reviewed at least quarterly. */
const REVIEW_WINDOW_DAYS = 90;
const DAY_MS = 24 * 60 * 60 * 1000;
const DATE_SHAPE = /^[0-9]{4}-[0-9]{2}-[0-9]{2}$/;
const EXCEPTIONS_PATH = fileURLToPath(new URL('../e2e/a11y-exceptions.json', import.meta.url));

/** The rule ids the installed axe-core knows; a name outside it can never fire. */
const KNOWN_RULE_IDS = new Set(axe.getRules().map((rule) => rule.ruleId));

/**
 * The UTC day a `YYYY-MM-DD` string names, or `null` when it names no day at all. `Date.UTC`
 * silently rolls `2026-02-30` into March, so the parsed fields are compared back to the text.
 */
function dayStamp(value) {
  if (typeof value !== 'string' || !DATE_SHAPE.test(value)) return null;
  const [year, month, day] = value.split('-').map(Number);
  const stamp = Date.UTC(year, month - 1, day);
  const parsed = new Date(stamp);
  const sameDay =
    parsed.getUTCFullYear() === year &&
    parsed.getUTCMonth() === month - 1 &&
    parsed.getUTCDate() === day;
  return sameDay ? stamp : null;
}

/** Problems with one entry; `seen` carries the (rule, selector) keys already used. */
function entryProblems(entry, index, todayStamp, seen) {
  const where = `exceptions[${index}]`;
  if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
    return [`${where} must be an object`];
  }
  const problems = [];
  for (const key of ENTRY_KEYS) {
    const value = entry[key];
    if (typeof value !== 'string' || value.trim() === '') {
      problems.push(`${where}.${key} must be a non-empty string`);
    }
  }
  for (const key of Object.keys(entry)) {
    if (!ENTRY_KEYS.includes(key)) problems.push(`${where} has unknown key "${key}"`);
  }
  const { rule, selector } = entry;
  if (typeof rule === 'string' && rule.trim() !== '' && !KNOWN_RULE_IDS.has(rule)) {
    problems.push(`${where}.rule "${rule}" is not an axe-core rule id`);
  }
  const reviewBy = dayStamp(entry['review-by']);
  if (typeof entry['review-by'] === 'string' && reviewBy === null) {
    problems.push(`${where}.review-by "${entry['review-by']}" is not a YYYY-MM-DD day`);
  } else if (reviewBy !== null) {
    if (reviewBy < todayStamp) {
      problems.push(`${where}.review-by ${entry['review-by']} has passed`);
    } else if (reviewBy > todayStamp + REVIEW_WINDOW_DAYS * DAY_MS) {
      problems.push(
        `${where}.review-by ${entry['review-by']} is later than ${REVIEW_WINDOW_DAYS} days out`,
      );
    }
  }
  if (typeof rule === 'string' && typeof selector === 'string') {
    const key = `${rule} at ${selector}`;
    if (seen.has(key)) problems.push(`${where} repeats ${key}`);
    else seen.add(key);
  }
  return problems;
}

/**
 * Every problem of one document for one day, in report order; an empty list means the list is
 * publishable. `today` is the `YYYY-MM-DD` day an entry is judged against.
 */
export function validate(document, today) {
  const todayStamp = dayStamp(today);
  if (todayStamp === null) return [`today "${today}" is not a YYYY-MM-DD day`];
  if (document === null || typeof document !== 'object' || Array.isArray(document)) {
    return ['the exception file must hold a JSON object'];
  }
  const problems = [];
  for (const key of Object.keys(document)) {
    if (!DOCUMENT_KEYS.includes(key)) problems.push(`unknown top-level key "${key}"`);
  }
  const entries = document.exceptions;
  if (!Array.isArray(entries)) {
    problems.push('"exceptions" must be an array');
    return problems;
  }
  const seen = new Set();
  entries.forEach((entry, index) => {
    problems.push(...entryProblems(entry, index, todayStamp, seen));
  });
  return problems;
}

/** Runs the check over one file and reports it; returns the exit code instead of exiting. */
function checkFile(path, today) {
  let document;
  try {
    document = JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    console.error(`a11y exceptions: ${path}: ${error.message}`);
    return 1;
  }
  const problems = validate(document, today);
  if (problems.length > 0) {
    for (const problem of problems) console.error(`a11y exceptions: ${problem}`);
    return 1;
  }
  console.log(`a11y exceptions: ${document.exceptions.length} entries, 0 problems (${today})`);
  return 0;
}

function main(argv) {
  const option = (name) => {
    const match = argv.find((argument) => argument.startsWith(`--${name}=`));
    return match === undefined ? undefined : match.slice(name.length + 3);
  };
  const today = option('today') ?? new Date().toISOString().slice(0, 10);
  const path = resolve(option('file') ?? EXCEPTIONS_PATH);
  process.exitCode = checkFile(path, today);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
