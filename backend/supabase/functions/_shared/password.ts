// The password policy, shared by every path that sets one.
//
// Length carries most of the strength. The character classes exist to rule out the
// obvious "12345678", not to be the main defence. A special character is allowed
// but deliberately not required: demanding one pushes people towards predictable
// endings and is awkward on the phone keyboards drivers use, for very little gain
// (NIST SP 800-63B makes the same argument).
//
// The upper bound is not a strength rule. Hashing is PBKDF2 at 100k iterations, so
// an unbounded password would let anyone spend the server's CPU at will.

export const MIN_PASSWORD_LENGTH = 8;
export const MAX_PASSWORD_LENGTH = 128;

/** The rule in one line, for clients that want to show it up front. */
export const PASSWORD_RULE =
  `At least ${MIN_PASSWORD_LENGTH} characters, with an uppercase letter, ` +
  `a lowercase letter and a number.`;

/** null when the password is acceptable, otherwise the reason to show the user. */
export function passwordProblem(pwd: string): string | null {
  if (pwd.length < MIN_PASSWORD_LENGTH) {
    return `Password must be at least ${MIN_PASSWORD_LENGTH} characters.`;
  }
  if (pwd.length > MAX_PASSWORD_LENGTH) {
    return `Password must be ${MAX_PASSWORD_LENGTH} characters or fewer.`;
  }
  if (!/[a-z]/.test(pwd)) return "Password needs a lowercase letter.";
  if (!/[A-Z]/.test(pwd)) return "Password needs an uppercase letter.";
  if (!/[0-9]/.test(pwd)) return "Password needs a number.";
  return null;
}
