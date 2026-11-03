namespace VolleyballClub.Web.Verification;

/// <summary>Explicitly isolated Docker regression environment; never enabled by Development/Production.</summary>
internal static class VerificationEnvironment
{
    internal const string Name = "Verification";
    internal const string Connection = "Data Source=/tmp/vicikyu-wasm-audit.db";

    internal static void Configure(WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetConnectionString("DefaultConnection") != Connection)
            throw new InvalidOperationException("Verification requires its isolated /tmp database.");
        if (ActivityManagementVerification.Enabled(builder.Environment, builder.Configuration))
        {
            builder.Services.AddSingleton<TimeProvider, ActivityManagementVerification.FixtureClock>();
        }
    }
}
