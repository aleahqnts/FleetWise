// Transactional email through Brevo's HTTP API. HTTP rather than SMTP because edge
// functions have no raw TCP sockets.
//
// Secrets: BREVO_API_KEY, BREVO_SENDER_EMAIL (must match a verified Brevo sender
// exactly, including case), and optional BREVO_SENDER_NAME.

const ENDPOINT = "https://api.brevo.com/v3/smtp/email";

export type Mail = {
  to: string;
  toName?: string;
  subject: string;
  html: string;
  text: string;
};

/**
 * Send one message. Returns false on any failure and never throws, so a mail
 * outage degrades the calling flow instead of turning into a 500 that would
 * tell an unauthenticated caller something about the account.
 */
export async function sendMail(m: Mail): Promise<boolean> {
  const key = Deno.env.get("BREVO_API_KEY");
  const senderEmail = Deno.env.get("BREVO_SENDER_EMAIL");
  const senderName = Deno.env.get("BREVO_SENDER_NAME") ?? "RouteSync";

  if (!key || !senderEmail) {
    console.error("[email] BREVO_API_KEY or BREVO_SENDER_EMAIL is not configured");
    return false;
  }

  try {
    const res = await fetch(ENDPOINT, {
      method: "POST",
      headers: {
        "api-key": key,
        "content-type": "application/json",
        "accept": "application/json",
      },
      body: JSON.stringify({
        sender: { name: senderName, email: senderEmail },
        to: [{ email: m.to, ...(m.toName ? { name: m.toName } : {}) }],
        subject: m.subject,
        htmlContent: m.html,
        textContent: m.text,
      }),
    });

    if (!res.ok) {
      // Body is logged because Brevo explains quota and sender-verification
      // refusals there, and those are the two failures worth acting on.
      console.error(`[email] Brevo returned ${res.status}: ${await res.text()}`);
      return false;
    }
    return true;
  } catch (e) {
    console.error(`[email] send failed: ${e}`);
    return false;
  }
}
