using Wobkey.Crush80;
using Wobkey.Crush80.Sdk.Exceptions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Wobkey.Crush80.Sdk.Tests")]

namespace Wobkey.Crush80.Sample;

/// <summary>Keeps every smoke failure, including failures during cleanup.</summary>
internal sealed class SmokeFailureReport
{
    private readonly List<(string Stage, Exception Error)> _failures = [];

    public int Count => _failures.Count;

    public bool WasCanceledOnly => _failures.Count == 1 && _failures[0].Error is OperationCanceledException;

    public void Capture(string stage, Exception error) => _failures.Add((stage, error));

    public async ValueTask<bool> AttemptAsync(string stage, Func<ValueTask> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception error)
        {
            Capture(stage, error);
            return false;
        }
    }

    public void WriteTo(TextWriter writer, bool controlAcquisitionStarted)
    {
        foreach (var (stage, error) in _failures)
        {
            writer.WriteLine($"{stage}: {error.Message}");
            if (error is StateRestoreException restoreError)
            {
                foreach (var failure in restoreError.Failures)
                    WriteExceptionChain(writer, failure.Field, failure.Error, 2);
                if (restoreError.InnerException is { } original)
                    WriteExceptionChain(writer, "Original operation", original, 2);
            }
            else if (error.InnerException is { } inner)
            {
                WriteExceptionChain(writer, "Cause", inner, 2);
            }
        }

        if (controlAcquisitionStarted && _failures.Count > 0)
            writer.WriteLine("WARNING: restoration could not be verified; check keyboard lighting/settings manually.");
    }

    private static void WriteExceptionChain(TextWriter writer, string label, Exception error, int indentation)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            for (var i = 0; i < indentation; i++)
                writer.Write(' ');
            writer.Write(label);
            writer.Write(": ");
            writer.WriteLine(current.Message);
            label = "Caused by";
            indentation += 2;
        }
    }
}
