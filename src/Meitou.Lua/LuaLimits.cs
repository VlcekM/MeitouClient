using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace Meitou.Lua;

/// <summary>
/// Execution budgets for a sandboxed Lua mod.
/// </summary>
public sealed class LuaLimits
{
    /// <summary>Maximum MoonSharp VM instructions allowed in one host call.</summary>
    public long InstructionsPerCall { get; init; } = 600_000;

    /// <summary>Maximum cumulative instructions allowed across one frame/tick.</summary>
    public long InstructionsPerTick { get; init; } = 2_000_000;
}

/// <summary>
/// Isolated, instruction-budgeted MoonSharp Lua 5.2 sandbox for a single KenshiLua mod.
/// Strips unsafe host libraries (io, os, debug, package, loadfile, dofile) and replaces
/// <c>math.random</c> with a deterministic per-mod PRNG.
/// </summary>
public sealed class LuaSandbox
{
    readonly LuaLimits _limits;
    readonly Action<string> _log;
    readonly BudgetDebugger _debugger;
    ulong _rngState;

    /// <summary>Human-readable mod identifier.</summary>
    public string Name { get; }

    /// <summary>Underlying MoonSharp script instance (host-internal).</summary>
    internal Script Script { get; }

    /// <summary>Whether this sandbox has faulted (budget exceeded or fatal load error).</summary>
    public bool Faulted { get; private set; }

    /// <summary>Short fault reason when <see cref="Faulted"/> is true.</summary>
    public string? FaultReason { get; private set; }

    /// <summary>Last error message from <see cref="Load"/> or <see cref="Eval"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Instructions consumed by the most recent call.</summary>
    public long LastCallInstructions => _debugger.CallInstructions;

    /// <summary>Creates a new sandboxed Lua environment.</summary>
    public LuaSandbox(string name, LuaLimits limits, ulong seed, Action<string> print, Action<string> log)
    {
        Name = name;
        _limits = limits;
        _log = log;
        _rngState = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        CoreModules safeModules = CoreModules.Basic
            | CoreModules.GlobalConsts
            | CoreModules.TableIterators
            | CoreModules.Metatables
            | CoreModules.String
            | CoreModules.Table
            | CoreModules.ErrorHandling
            | CoreModules.Math
            | CoreModules.Coroutine
            | CoreModules.Bit32;

        Script = new Script(safeModules);
        Script.Options.DebugPrint = print;

        // Strip file-loading globals that bypass SafePath
        Script.Globals["dofile"] = DynValue.Nil;
        Script.Globals["loadfile"] = DynValue.Nil;

        // Replace math.random / math.randomseed with deterministic xorshift64*
        Table mathTbl = Script.Globals.Get("math").Table;
        mathTbl["randomseed"] = (Func<CallbackArguments, DynValue>)(a =>
        {
            long s = a.Count > 0 && a[0].Type == DataType.Number ? (long)a[0].Number : 1;
            _rngState = unchecked((ulong)s) ^ 0x9E3779B97F4A7C15UL;
            if (_rngState == 0) _rngState = 1;
            return DynValue.Nil;
        });
        mathTbl["random"] = (Func<CallbackArguments, DynValue>)(a =>
        {
            double u = NextUnitDouble();
            if (a.Count == 0) return DynValue.NewNumber(u);
            if (a.Count == 1)
            {
                long hi = (long)a[0].Number;
                if (hi < 1) throw new ScriptRuntimeException("interval is empty");
                return DynValue.NewNumber(1 + (long)(u * hi));
            }
            long lo = (long)a[0].Number;
            long max = (long)a[1].Number;
            if (max < lo) throw new ScriptRuntimeException("interval is empty");
            return DynValue.NewNumber(lo + (long)(u * (max - lo + 1)));
        });

        _debugger = new BudgetDebugger();
        Script.AttachDebugger(_debugger);
    }

    /// <summary>Exposes a host callback either as a global or inside a named global table.</summary>
    public void Expose(string tableName, string funcName, Func<CallbackArguments, DynValue> fn)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            Script.Globals[funcName] = fn;
            return;
        }
        DynValue existing = Script.Globals.Get(tableName);
        Table tbl;
        if (existing.Type == DataType.Table)
        {
            tbl = existing.Table;
        }
        else
        {
            tbl = new Table(Script);
            Script.Globals[tableName] = tbl;
        }
        tbl[funcName] = fn;
    }

    /// <summary>Loads and executes a chunk of Lua source under the instruction budget.</summary>
    public DynValue Load(string chunkName, string code)
    {
        if (Faulted) return DynValue.Nil;
        LastError = null;
        _debugger.Begin(_limits.InstructionsPerCall);
        try
        {
            return Script.DoString(code, null, chunkName);
        }
        catch (BudgetExceededException e)
        {
            Faulted = true;
            FaultReason = "budget";
            LastError = e.Message;
            _log($"[Meitou.Lua:{Name}] faulted ({FaultReason}): {e.Message}");
            return DynValue.Nil;
        }
        catch (InterpreterException e)
        {
            LastError = e.DecoratedMessage ?? e.Message;
            return DynValue.Nil;
        }
    }

    /// <summary>Evaluates a Lua snippet under the instruction budget.</summary>
    public DynValue Eval(string code) => Load("eval", code);

    /// <summary>
    /// Resolves <paramref name="relativePath"/> strictly inside <paramref name="rootFolder"/>,
    /// rejecting path traversal (<c>..</c>), rooted paths, and symlinks/reparse points.
    /// </summary>
    public static string? SafePath(string rootFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(relativePath)) return null;
        string clean = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(clean) || clean.StartsWith('/') || clean.Contains(':', StringComparison.Ordinal)) return null;
        foreach (string seg in clean.Split('/'))
        {
            if (seg == "..") return null;
        }
        try
        {
            string fullRoot = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, clean));
            if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return null;
            return candidate;
        }
        catch (Exception)
        {
            return null;
        }
    }

    double NextUnitDouble()
    {
        ulong x = _rngState;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _rngState = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
    }

    sealed class BudgetExceededException(string message) : Exception(message);

    sealed class BudgetDebugger : IDebugger
    {
        long _budget;
        public long CallInstructions { get; private set; }

        public void Begin(long budget)
        {
            _budget = budget;
            CallInstructions = 0;
        }

        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;
        public void SetDebugService(DebugService debugService) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SetByteCode(string[] byteCode) { }
        public bool IsPauseRequested()
        {
            CallInstructions++;
            if (CallInstructions > _budget)
            {
                throw new BudgetExceededException($"exceeded {_budget} instructions in one call");
            }
            return false;
        }
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
        public DebuggerAction GetAction(int ip, SourceRef sourceref) => new() { Action = DebuggerAction.ActionType.Run };
        public void SignalExecutionEnded() { }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex) { }
        public List<DynamicExpression> GetWatchItems() => [];
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }
}
