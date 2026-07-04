namespace FleetWise.Services
{
    // Single source of truth for the temporary password handed to brand-new accounts.
    // A user is "still on the temp password" if a successful login used exactly this value
    // (auth already proved the hash matches), so no extra DB column is needed to force the
    // first-login change. The change form rejects this value so nobody can keep it.
    public static class PasswordPolicy
    {
        public const string TemporaryPassword = "@Temp123";

        // Claim stamped on the auth cookie while the user must still change their temp password.
        public const string MustChangeClaim = "pwd_temp";
    }
}
