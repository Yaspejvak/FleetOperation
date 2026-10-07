using Fserp.FleetOperations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// The one place this host may apply Entity Framework migrations while starting, and the gate that keeps
/// it shut unless an environment asks for it explicitly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default is "do not migrate" (lead decision L-31).</b> docs/getting-started.md states that
/// migrations are a deployment step and never run at host start, and that remains true for every path that
/// does not set <see cref="MigrateOnStartupKey"/>: a deployment that forgets the variable does not migrate,
/// and it does not silently migrate either. <c>appsettings.json</c> ships the key as <c>false</c> so the
/// decision is visible rather than implied by an absent key.
/// </para>
/// <para>
/// <c>docker-compose.yml</c> is the only thing in this repository that turns it on, with
/// <c>Database__MigrateOnStartup=true</c> on the application service, because a throwaway stack brings up
/// an empty PostgreSQL that nobody is going to migrate by hand. Nothing else sets it.
/// </para>
/// <para>
/// Applying migrations from the application process is deliberately not the recommended shape for a real
/// deployment: several instances starting at once race on the same schema, and the runtime role then needs
/// DDL rights it should not keep. Use <c>dotnet ef database update</c>, or a job that runs once, there.
/// </para>
/// </remarks>
public static class DatabaseStartup
{
    /// <summary>
    /// The configuration key that enables migration on startup. As an environment variable:
    /// <c>Database__MigrateOnStartup</c>.
    /// </summary>
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    /// <summary>
    /// Whether this host should migrate while starting. <see langword="false"/> when the key is absent or
    /// blank; <see langword="true"/> only for a parseable <c>true</c>.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// The key carries a value that is not a boolean. Someone meant to set the gate and mistyped it;
    /// reading that as "off" would be the silent migration decision L-31 rules out, in reverse.
    /// </exception>
    /// <remarks>
    /// Deliberately not <c>configuration.GetValue&lt;bool&gt;(key, false)</c>. That overload throws
    /// <see cref="InvalidOperationException"/> for an <b>empty</b> value, so
    /// <c>Database__MigrateOnStartup=</c> — a variable declared with no value, which an environment file
    /// or a deployment manifest produces easily — would fail the whole host at boot instead of taking the
    /// default. "Set to nothing" is "not set", and the default applies.
    /// </remarks>
    public static bool MigratesOnStartup(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[MigrateOnStartupKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value.Trim(), out var migrates)
            ? migrates
            : throw new InvalidOperationException(
                $"{MigrateOnStartupKey} is '{value}', which is not true or false. Leave it unset to keep "
                + "the default (do not migrate), or set it to true to migrate while starting.");
    }

    /// <summary>
    /// Applies every pending migration, but only when <see cref="MigratesOnStartup"/> says so. Does
    /// nothing at all otherwise — it does not open a scope, resolve the context or touch the database.
    /// </summary>
    /// <param name="app">The built application.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    public static async Task ApplyMigrationsIfRequestedAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!MigratesOnStartup(app.Configuration))
        {
            return;
        }

        app.Logger.LogInformation(
            "{Key} is set: applying pending migrations before the host starts serving.",
            MigrateOnStartupKey);

        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
