namespace FleetWise.Services
{
    /// <summary>
    /// The temporary password issued to new accounts.
    /// </summary>
    /// <remarks>
    /// A user is still on the temporary password when a successful sign-in used exactly
    /// this value, since authentication has already proved the hash matches. That removes
    /// the need for a column tracking whether the password has been changed.
    ///
    /// The change form rejects this value, so it cannot be kept.
    /// </remarks>
    public static class PasswordPolicy
    {
        public const string TemporaryPassword = "@Temp123";

        // Stamped on the authentication cookie while the password still has to be changed.
        public const string MustChangeClaim = "pwd_temp";
    }
}
