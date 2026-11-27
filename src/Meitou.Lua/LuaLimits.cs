using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace Meitou.Lua;

/// <summary>
/// Execution budgets for a sandboxed Lua mod.
/// </summary>
public sealed class LuaLimits
{
    /// <summary>
    /// Maximum MoonSharp VM instructions allowed in one host call.
    /// </summary>
    public long InstructionsPerCall { get; init; } = 600_000;

    /// <summary>
    /// Maximum cumulative instructions allowed across one frame/tick.
    /// </summary>
    public long InstructionsPerTick { get; init; } = 2_000_000;
}

/// <summary>
/// Isolated, instruction-budgeted MoonSharp Lua 5.2 sandbox for a single KenshiLua mod.
/// Strips unsafe host libraries (io, os, debug, package, loadfile, dofile) and replaces
/// <c>math.random</c> with a deterministic per-mod PRNG.
/// </summary>
public sealed class LuaSandbox
{
    #region Sandbox

    /// <summary>
    /// The instruction budgets.
    /// </summary>
    readonly LuaLimits _limits;

    /// <summary>
    /// Receives the host's fault log lines.
    /// </summary>
    readonly Action<string> _log;

    /// <summary>
    /// Counts instructions and aborts a call that goes over its budget.
    /// </summary>
    readonly BudgetDebugger _debugger;

    /// <summary>
    /// Human-readable mod identifier.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Underlying MoonSharp script instance (host-internal).
    /// </summary>
    internal Script Script { get; }

    /// <summary>
    /// Whether this sandbox has faulted (budget exceeded or fatal load error).
    /// </summary>
    public bool Faulted { get; private set; }

    /// <summary>
    /// Short fault reason when <see cref="Faulted"/> is true.
    /// </summary>
    public string? FaultReason { get; private set; }

    /// <summary>
    /// Last error message from <see cref="Load"/> or <see cref="Eval"/>.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Instructions consumed by the most recent call.
    /// </summary>
    public long LastCallInstructions => _debugger.CallInstructions;

    /// <summary>
    /// Creates a new sandboxed Lua environment.
    /// </summary>
    public LuaSandbox(string name, LuaLimits limits, ulong seed, Action<string> print, Action<string> log)
    {
        Name = name;
        _limits = limits;
        _log = log;
        var random = new LuaRandom(seed);

        Script = new Script(SafeModules());
        Script.Options.DebugPrint = print;

        // Strip file-loading globals that bypass SafePath
        Script.Globals["dofile"] = DynValue.Nil;
        Script.Globals["loadfile"] = DynValue.Nil;

        ReplaceMathRandom(random);

        _debugger = new BudgetDebugger();
        Script.AttachDebugger(_debugger);
    }

    /// <summary>
    /// Exposes a host callback either as a global or inside a named global table.
    /// </summary>
    public void Expose(string tableName, string funcName, Func<CallbackArguments, DynValue> fn)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            Script.Globals[funcName] = fn;
            return;
        }

        GlobalTable(tableName)[funcName] = fn;
    }

    /// <summary>
    /// Loads and executes a chunk of Lua source under the instruction budget.
    /// </summary>
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
            Fault(e);
            return DynValue.Nil;
        }
        catch (InterpreterException e)
        {
            LastError = e.DecoratedMessage ?? e.Message;
            return DynValue.Nil;
        }
    }

    /// <summary>
    /// Evaluates a Lua snippet under the instruction budget.
    /// </summary>
    public DynValue Eval(string code) => Load("eval", code);

    /// <summary>
    /// Invokes an already-loaded Lua function under the instruction budget without compiling new bytecode.
    /// </summary>
    public DynValue Call(DynValue fn, params object[] args)
    {
        if (Faulted || fn.IsNil()) return DynValue.Nil;

        LastError = null;
        _debugger.Begin(_limits.InstructionsPerCall);
        try
        {
            return Script.Call(fn, args);
        }
        catch (BudgetExceededException e)
        {
            Fault(e);
            return DynValue.Nil;
        }
        catch (InterpreterException e)
        {
            LastError = e.DecoratedMessage ?? e.Message;
            return DynValue.Nil;
        }
    }

    /// <summary>
    /// Invokes a global Lua function by name under the instruction budget without compiling new bytecode.
    /// </summary>
    public DynValue CallGlobal(string funcName, params object[] args) =>
        Faulted ? DynValue.Nil : Call(Script.Globals.Get(funcName), args);

    /// <summary>
    /// The MoonSharp modules a mod may use: no io, os, debug or package.
    /// </summary>
    static CoreModules SafeModules() =>
        CoreModules.Basic
        | CoreModules.GlobalConsts
        | CoreModules.TableIterators
        | CoreModules.Metatables
        | CoreModules.String
        | CoreModules.Table
        | CoreModules.ErrorHandling
        | CoreModules.Math
        | CoreModules.Coroutine
        | CoreModules.Bit32;

    /// <summary>
    /// Replaces math.random / math.randomseed with the deterministic xorshift64* of <paramref name="random"/>.
    /// </summary>
    void ReplaceMathRandom(LuaRandom random)
    {
        Table mathTbl = Script.Globals.Get("math").Table;
        mathTbl["randomseed"] = (Func<CallbackArguments, DynValue>)random.Seed;
        mathTbl["random"] = (Func<CallbackArguments, DynValue>)random.Next;
    }

    /// <summary>
    /// The global table named <paramref name="tableName"/>, created when the script has no table of that name.
    /// </summary>
    Table GlobalTable(string tableName)
    {
        DynValue existing = Script.Globals.Get(tableName);
        if (existing.Type == DataType.Table) return existing.Table;

        var tbl = new Table(Script);
        Script.Globals[tableName] = tbl;

        return tbl;
    }

    /// <summary>
    /// Marks the sandbox as faulted by a budget overrun and logs it.
    /// </summary>
    void Fault(BudgetExceededException e)
    {
        Faulted = true;
        FaultReason = "budget";
        LastError = e.Message;
        _log($"[Meitou.Lua:{Name}] faulted ({FaultReason}): {e.Message}");
    }

    #endregion

    #region Safe paths

    /// <summary>
    /// Resolves <paramref name="relativePath"/> strictly inside <paramref name="rootFolder"/>,
    /// rejecting path traversal (<c>..</c>), rooted paths, and symlinks/reparse points.
    /// </summary>
    public static string? SafePath(string rootFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(relativePath)) return null;

        string clean = relativePath.Trim().Replace('\\', '/');
        if (!IsRelativeAndContained(clean)) return null;

        try
        {
            return ResolveInside(rootFolder, clean);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the slash-separated path is relative, has no drive or colon and no <c>..</c> segment.
    /// </summary>
    static bool IsRelativeAndContained(string clean)
    {
        if (Path.IsPathRooted(clean) || clean.StartsWith('/') || clean.Contains(':', StringComparison.Ordinal)) return false;

        foreach (string seg in clean.Split('/'))
        {
            if (seg == "..") return false;
        }

        return true;
    }

    /// <summary>
    /// The full path of <paramref name="clean"/> under <paramref name="rootFolder"/>; null when it lands outside or is a reparse point.
    /// </summary>
    static string? ResolveInside(string rootFolder, string clean)
    {
        string fullRoot = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, clean));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return null;

        return candidate;
    }

    #endregion

    #region Instruction budget

    /// <summary>
    /// Thrown by <see cref="BudgetDebugger"/> when a call goes over its instruction budget.
    /// </summary>
    sealed class BudgetExceededException(string message) : Exception(message);

    /// <summary>
    /// A MoonSharp debugger that only counts instructions and throws once the call's budget is spent.
    /// </summary>
    sealed class BudgetDebugger : IDebugger
    {
        /// <summary>
        /// The instructions the current call may run.
        /// </summary>
        long _budget;

        /// <summary>
        /// Instructions run by the current call.
        /// </summary>
        public long CallInstructions { get; private set; }

        /// <summary>
        /// Starts a call with <paramref name="budget"/> instructions.
        /// </summary>
        public void Begin(long budget)
        {
            _budget = budget;
            CallInstructions = 0;
        }

        /// <summary>
        /// The debugger only needs the source-code capability.
        /// </summary>
        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;

        /// <summary>
        /// Not used: the debugger keeps no service.
        /// </summary>
        public void SetDebugService(DebugService debugService) { }

        /// <summary>
        /// Not used: the debugger shows no source.
        /// </summary>
        public void SetSourceCode(SourceCode sourceCode) { }

        /// <summary>
        /// Not used: the debugger shows no byte code.
        /// </summary>
        public void SetByteCode(string[] byteCode) { }

        /// <summary>
        /// Called once per VM instruction: counts it and throws past the budget; never pauses.
        /// </summary>
        public bool IsPauseRequested()
        {
            CallInstructions++;
            if (CallInstructions > _budget)
                throw new BudgetExceededException($"exceeded {_budget} instructions in one call");

            return false;
        }

        /// <summary>
        /// Not used: runtime exceptions are left to the interpreter.
        /// </summary>
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;

        /// <summary>
        /// Always keeps running.
        /// </summary>
        public DebuggerAction GetAction(int ip, SourceRef sourceref) => new() { Action = DebuggerAction.ActionType.Run };

        /// <summary>
        /// Not used: nothing waits for the end of execution.
        /// </summary>
        public void SignalExecutionEnded() { }

        /// <summary>
        /// Not used: no watches.
        /// </summary>
        public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex) { }

        /// <summary>
        /// Not used: no watches.
        /// </summary>
        public List<DynamicExpression> GetWatchItems() => [];

        /// <summary>
        /// Not used: no breakpoints.
        /// </summary>
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }

    #endregion
}
