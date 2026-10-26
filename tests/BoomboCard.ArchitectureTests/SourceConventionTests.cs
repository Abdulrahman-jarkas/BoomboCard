using System.Text.RegularExpressions;

namespace BoomboCard.ArchitectureTests;

/// <summary>
/// Rules that are about source text rather than types, so NetArchTest cannot see them.
///
/// Each one guards against a specific Onyx defect that a compiler will happily accept.
///
/// <para>
/// <b>Every project under <c>src/</c> is scanned, discovered at run time.</b> This used to
/// consult a hardcoded five-element array of project names, which was correct on the day it was
/// written and would have silently stopped covering the codebase the moment a seventh project
/// appeared — a new module would have been free to write <c>DateTime.Now</c>, <c>.Result</c>,
/// <c>async void</c> and <c>catch { }</c> with the suite still green. A stale array enforces
/// exactly as much as a stale document, which is the whole subject of Onyx X1–X15.
/// </para>
/// </summary>
public sealed partial class SourceConventionTests
{

    /// <summary>
    /// Onyx A29/A30: it shipped a <c>ClockHelper</c> that wrapped <c>DateTime.UtcNow</c> and used
    /// it in zero places, so no time-dependent rule — promo expiry, report windows, session
    /// lifetime — could be unit tested. It also wrote <c>DateTime.Now</c>, server LOCAL time,
    /// into a field named <c>OrderDateUtc</c>, then compared it against a local day boundary in a
    /// report.
    ///
    /// Time is an input here: domain methods take <c>nowUtc</c>, handlers get it from an injected
    /// <see cref="TimeProvider"/>. The composition root is the one place allowed to name the
    /// system clock.
    /// </summary>
    [Fact]
    public void NothingCallsDateTimeNowOrUtcNowDirectly()
    {
        var offenders = Scan(
            DateTimePattern(),
            // TimeProvider.System in the composition root is the one legitimate reference.
            allowedFiles: ["DependencyInjection.cs"]);

        Assert.True(
            offenders.Count == 0,
            "Use an injected TimeProvider, and pass nowUtc into the domain. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Onyx A13: <c>_gateway.ChargeAsync(...).Result</c> inside the payment retry loop blocked a
    /// thread-pool thread for the duration of an HTTP call — the classic thread-pool starvation
    /// deadlock, on the hottest path in the system.
    /// </summary>
    [Fact]
    public void NothingBlocksOnAsyncWithResultOrWait()
    {
        var offenders = Scan(BlockingPattern());

        Assert.True(
            offenders.Count == 0,
            "Await it. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Onyx D7: <c>private async void ReplenishAsync(...)</c>. The caller could not await it, so
    /// the job logged success before any work ran, the enclosing scope closed the database
    /// connection while the work was still using it, and an exception inside it terminated the
    /// entire worker process.
    /// </summary>
    [Fact]
    public void NothingDeclaresAsyncVoid()
    {
        var offenders = Scan(AsyncVoidPattern());

        Assert.True(
            offenders.Count == 0,
            "async void cannot be awaited and its exceptions kill the process. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Onyx A16: <c>catch { }</c> in the payment retry loop destroyed all diagnostic information
    /// about every gateway failure, and dome-gym's
    /// <c>catch (EventualConsistencyException) { }</c> rolled a transaction back and told nobody
    /// after the client had already received a 200.
    /// </summary>
    [Fact]
    public void NoEmptyCatchBlocks()
    {
        var offenders = Scan(EmptyCatchPattern());

        Assert.True(
            offenders.Count == 0,
            "Handle it, log it, or rethrow. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Onyx A8: <c>_ = Task.Run(() =&gt; SendConfirmationEmail(...))</c> on a method whose body
    /// was <c>throw new NotImplementedException()</c>. Discarded, so the exception was never
    /// observed — no customer was ever contacted and nothing recorded that. Side effects go
    /// through the outbox.
    /// </summary>
    [Fact]
    public void NothingFiresAndForgetsWithTaskRun()
    {
        var offenders = Scan(FireAndForgetPattern());

        Assert.True(
            offenders.Count == 0,
            "Use the outbox for work that must happen but must not block the caller. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Onyx D12 called <c>EnsureCreated()</c> from two processes against the same SQLite file. It
    /// does nothing if the database exists, so every schema change required deleting the database
    /// and losing all data, and there was no migration history or upgrade path.
    /// Tests may use it; production code may not.
    /// </summary>
    [Fact]
    public void ProductionCodeDoesNotCallEnsureCreated()
    {
        var offenders = Scan(EnsureCreatedPattern());

        Assert.True(
            offenders.Count == 0,
            "Use migrations. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <b>Onyx S8 — the single most dangerous line in that codebase.</b>
    ///
    /// <code>
    /// ServerCertificateCustomValidationCallback = (_, _, _, _) =&gt; true
    /// </code>
    ///
    /// <para>
    /// On the <i>payment gateway client</i>. That callback is the one control preventing an
    /// attacker impersonating the payment provider; with it returning <c>true</c> for every
    /// certificate, card tokens and charge requests can be read and altered on the wire by anyone
    /// positioned to do so. The obligation line is unambiguous: <i>"never override certificate
    /// validation. If a sandbox needs a private CA, trust that CA at the machine level in that
    /// environment only."</i>
    /// </para>
    ///
    /// <para>
    /// <b>Why this is a source-text rule rather than a review item.</b> It is three tokens, it
    /// looks like configuration, it makes a stubborn sandbox work immediately, and it is the
    /// first result for every search about TLS errors in development. It will be suggested again.
    /// The only durable defence is a test that fails the build — and because the scan covers
    /// every project under <c>src/</c> discovered at run time, it already covers modules that do
    /// not exist yet.
    /// </para>
    ///
    /// <para>
    /// The pattern also catches <c>DangerousAcceptAnyServerCertificateValidator</c>, which is the
    /// same thing with a name that at least warns you, and revocation checking being configured
    /// at all.
    /// </para>
    /// </summary>
    [Fact]
    public void NothingDisablesTlsCertificateValidation()
    {
        var offenders = Scan(CertificateValidationPattern());

        Assert.True(
            offenders.Count == 0,
            "NEVER override certificate validation. This is Onyx S8 — it was set on the payment "
            + "gateway client, which put card tokens on the wire for anyone positioned to read "
            + "them. If a sandbox needs a private CA, trust that CA at the machine level in that "
            + "environment only; code ships everywhere. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// A status is never assigned from a string literal.
    ///
    /// <para>
    /// <b>Onyx M1 is one statement:</b>
    /// <c>var payment = new Payment { ..., Status = "Paid", ... }</c>, placed after a retry loop
    /// that never inspected its result. A declined card, a provider outage, a DNS failure and
    /// three timeouts all reached that line. <b>A39</b> is why it was possible at all —
    /// <c>Payment.Status</c> was <c>public string Status { get; set; } = "Pending"</c>, while
    /// <c>OrderStatus</c> next to it was a proper enum.
    /// </para>
    ///
    /// <para>
    /// The structural answer is that every status in this system is a value object with a private
    /// constructor, reachable only through guarded transitions — and that a payment cannot reach
    /// a settled status without a <c>ProviderReference</c>, which can only be built from a
    /// provider response. This test guards what the type system cannot: that nobody reintroduces
    /// a free-text status by assigning one.
    /// </para>
    /// </summary>
    [Fact]
    public void NoStatusIsAssignedFromAStringLiteral()
    {
        var offenders = Scan(StatusLiteralPattern());

        Assert.True(
            offenders.Count == 0,
            "A status is reached through a guarded transition, never assigned from a literal. "
            + "This is Onyx M1 and A39. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ----------------------------------------------------------------- scanning

    private static List<string> Scan(Regex pattern, string[]? allowedFiles = null)
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            if (allowedFiles?.Contains(Path.GetFileName(file)) == true)
            {
                continue;
            }

            var lines = File.ReadAllLines(file);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                // Skip comments and XML docs — several of them quote the banned patterns on
                // purpose, to explain why they are banned.
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(line))
                {
                    offenders.Add($"  {Path.GetFileName(file)}:{index + 1}  {trimmed}");
                }
            }
        }

        return offenders;
    }

    private static IEnumerable<string> ProductionSourceFiles()
    {
        var source = Path.Combine(FindRepositoryRoot(), "src");

        Assert.True(Directory.Exists(source), $"Expected production source under {source}.");

        foreach (var project in Directory.EnumerateDirectories(source))
        {
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                // Generated migrations and build output are not hand-written code.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BoomboCard.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [GeneratedRegex(@"\bDateTime(Offset)?\s*\.\s*(Now|UtcNow|Today)\b")]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"\)\s*\.\s*(Result|GetAwaiter\(\)\s*\.\s*GetResult\(\))|\.\s*Wait\(\s*\)")]
    private static partial Regex BlockingPattern();

    [GeneratedRegex(@"\basync\s+void\b")]
    private static partial Regex AsyncVoidPattern();

    [GeneratedRegex(@"\bcatch\s*(\([^)]*\))?\s*\{\s*\}")]
    private static partial Regex EmptyCatchPattern();

    [GeneratedRegex(@"_\s*=\s*Task\s*\.\s*Run")]
    private static partial Regex FireAndForgetPattern();

    [GeneratedRegex(@"\.\s*EnsureCreated(Async)?\s*\(")]
    private static partial Regex EnsureCreatedPattern();

    [GeneratedRegex(
        @"ServerCertificateCustomValidationCallback"
        + @"|DangerousAcceptAnyServerCertificateValidator"
        + @"|CheckCertificateRevocationList")]
    private static partial Regex CertificateValidationPattern();

    [GeneratedRegex(@"\bStatus\s*=\s*""[A-Za-z]+""")]
    private static partial Regex StatusLiteralPattern();
}
