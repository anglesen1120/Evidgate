export type ExactNumber = number | bigint | string | null | undefined;

export class ExactNumberFormatter {
  private readonly integer: Intl.NumberFormat;
  private readonly number: Intl.NumberFormat;
  private readonly decimal: string;
  private readonly digits: readonly string[];
  readonly group: string;

  constructor(locale: string) {
    this.integer = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 });
    this.number = new Intl.NumberFormat(locale, { maximumSignificantDigits: 21 });
    this.decimal =
      this.number.formatToParts(1.1).find((part) => part.type === 'decimal')?.value ?? '.';
    this.group =
      this.number.formatToParts(1000).find((part) => part.type === 'group')?.value ?? ',';
    this.digits = Array.from({ length: 10 }, (_, digit) => this.integer.format(digit));
  }

  format(value: ExactNumber): string | null {
    if (typeof value === 'number') return Number.isFinite(value) ? this.number.format(value) : null;
    if (typeof value === 'bigint') return this.integer.format(value);
    if (typeof value !== 'string') return null;
    const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(value);
    if (!match) return null;
    // Decimal wire strings must not pass through Number: quota facts can exceed its precision.
    const integer = BigInt(`${match[1]}${match[2]}`);
    const formatted = this.integer.format(integer === 0n && match[1] === '-' ? -0 : integer);
    const fraction = match[3];
    if (fraction === undefined) return formatted;
    return `${formatted}${this.decimal}${fraction.replace(/\d/g, (digit) => this.digits[Number(digit)]!)}`;
  }
}
