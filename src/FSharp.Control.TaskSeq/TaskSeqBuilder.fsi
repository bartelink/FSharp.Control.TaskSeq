namespace FSharp.Control

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Tasks.Sources
open System.Runtime.CompilerServices
open System.Collections.Generic

open FSharp.Core.CompilerServices

[<AutoOpen>]
module Internal =

    /// <summary>
    /// Setting from environment variable <see cref="TASKSEQ_LOG_VERBOSE" />, which,
    /// when set, enables (very) verbose printing of flow and state
    /// </summary>
    val initVerbose: unit -> bool

    /// Call MoveNext on an IAsyncStateMachine by reference
    val inline moveNextRef: x: byref<#IAsyncStateMachine> -> unit

    /// F# requires that we implement interfaces even on an abstract class.
    val inline raiseNotImpl: unit -> 'a

/// <summary>
/// Result of any <see cref="taskSeq" /> computation expression, alias for <see cref="T:System.IAsyncEnumerable&lt;'T&gt;" />.
/// </summary>
type taskSeq<'T> = IAsyncEnumerable<'T>

/// TaskSeqCode type alias of ResumableCode delegate type, specially recognized by the F# compiler
and ResumableTSC<'T> = ResumableCode<TaskSeqStateMachineData<'T>, unit>

/// <summary>
/// Contains the state data for the <see cref="taskSeq" /> computation expression builder.
/// For use in this library only. Required by the <see cref="TaskSeqBuilder.Run" /> method.
/// </summary>
and TaskSeqStateMachine<'T> = ResumableStateMachine<TaskSeqStateMachineData<'T>>

/// <summary>
/// Contains the state data for the <see cref="taskSeq" /> computation expression builder.
/// For use in this library only. Required by the <see cref="TaskSeqBuilder.Run" /> method.
/// </summary>
and [<Class; NoComparison; NoEquality>] TaskSeqStateMachineData<'T> =

    new: unit -> TaskSeqStateMachineData<'T>

    [<DefaultValue(false)>]
    val mutable cancellationToken: CancellationToken

    /// Keeps track of the objects that need to be disposed off on IAsyncDispose.
    [<DefaultValue(false)>]
    val mutable disposalStack: ResizeArray<(unit -> Task)>

    [<DefaultValue(false)>]
    val mutable awaiter: ICriticalNotifyCompletion

    [<DefaultValue(false)>]
    val mutable promiseOfValueOrEnd: ManualResetValueTaskSourceCore<bool>

    /// Helper struct providing methods for awaiting 'next' in async iteration scenarios.
    [<DefaultValue(false)>]
    val mutable builder: AsyncIteratorMethodBuilder

    /// Whether or not a full iteration through the IAsyncEnumerator has completed
    [<DefaultValue(false)>]
    val mutable completed: bool

    /// Used by the AsyncEnumerator interface to return the Current value when
    /// IAsyncEnumerator.Current is called
    [<DefaultValue(false)>]
    val mutable current: ValueOption<'T>

    /// A reference to 'self', because otherwise we can't use byref in the resumable code.
    [<DefaultValue(false)>]
    val mutable boxedSelf: TaskSeqBase<'T>

    member PopDispose: unit -> unit

    member PushDispose: disposer: (unit -> Task) -> unit

/// <summary>
/// Abstract base class for <see cref="TaskSeq&lt;'Machine, 'T&gt;" />.
/// For use by this library only, should not be used directly in user code. Its operation depends highly on resumable state.
/// </summary>
and [<AbstractClass; NoEquality; NoComparison>] TaskSeqBase<'T> =
    interface IValueTaskSource<bool>
    interface IValueTaskSource
    interface IAsyncStateMachine
    interface IAsyncEnumerable<'T>
    interface IAsyncEnumerator<'T>

    new: unit -> TaskSeqBase<'T>

    abstract MoveNextAsyncResult: unit -> ValueTask<bool>

/// <summary>
/// Main implementation of generic <see cref="T:System.IAsyncEnumerable&lt;'T&gt;" /> and related interfaces,
/// which forms the meat of the logic behind <see cref="taskSeq" /> computation expresssions.
/// For use by this library only, should not be used directly in user code. Its operation depends highly on resumable state.
/// </summary>
and [<NoComparison; NoEquality>] TaskSeq<'Machine, 'T
    when 'Machine :> IAsyncStateMachine and 'Machine :> IResumableStateMachine<TaskSeqStateMachineData<'T>>> =
    inherit TaskSeqBase<'T>
    interface IAsyncEnumerator<'T>
    interface IAsyncEnumerable<'T>
    interface IAsyncStateMachine
    interface IValueTaskSource<bool>
    interface IValueTaskSource

    new: unit -> TaskSeq<'Machine, 'T>

    [<DefaultValue(false)>]
    val mutable _initialMachine: 'Machine

    /// Keeps the active state machine.
    [<DefaultValue(false)>]
    val mutable _machine: 'Machine

    //new: unit -> TaskSeq<'Machine, 'T>
    member InitMachineData: ct: CancellationToken * machine: byref<'Machine> -> unit
    override MoveNextAsyncResult: unit -> ValueTask<bool>

/// <summary>
/// Main builder class for the <see cref="taskSeq" /> computation expression.
/// </summary>
[<Class>]
type TaskSeqBuilder =

    member inline Combine: task1: ResumableTSC<'T> * task2: ResumableTSC<'T> -> ResumableTSC<'T>
    member inline Delay: f: (unit -> ResumableTSC<'T>) -> ResumableTSC<'T>
    member inline Run: code: ResumableTSC<'T> -> taskSeq<'T>
    member inline TryFinally: body: ResumableTSC<'T> * compensation: (unit -> unit) -> ResumableTSC<'T>
    member inline TryFinallyAsync: body: ResumableTSC<'T> * compensation: (unit -> Task) -> ResumableTSC<'T>
    member inline TryWith: body: ResumableTSC<'T> * catch: (exn -> ResumableTSC<'T>) -> ResumableTSC<'T>
    member inline Using: disp: 'a * body: ('a -> ResumableTSC<'T>) -> ResumableTSC<'T> when 'a :> IAsyncDisposable
    member inline While: condition: (unit -> bool) * body: ResumableTSC<'T> -> ResumableTSC<'T>
    /// Used by `For`. F# currently doesn't support `while!`, so this cannot be called directly from the CE
    member inline WhileAsync: condition: (unit -> ValueTask<bool>) * body: ResumableTSC<'T> -> ResumableTSC<'T>
    member inline Yield: v: 'T -> ResumableTSC<'T>
    member inline Zero: unit -> ResumableTSC<'T>

[<AutoOpen>]
module TaskSeqBuilder =

    /// <summary>
    /// Builds an asynchronous task sequence based on <see cref="IAsyncEnumerable&lt;'T&gt;" /> using computation expression syntax.
    /// </summary>
    val taskSeq: TaskSeqBuilder

/// <summary>
/// Contains low priority extension methods for the main builder class for the <see cref="taskSeq" /> computation expression.
/// The <see cref="LowPriority" />, <see cref="MediumPriority" /> and <see cref="HighPriority" /> modules are not meant to be
/// accessed directly from user code. They solely serve to disambiguate overload resolution inside the <see cref="taskSeq" /> computation expression.
/// </summary>
[<AutoOpen>]
module LowPriority =
    type TaskSeqBuilder with

        [<NoEagerConstraintApplication>]
        member inline Bind< ^TaskLike, 'TResult1, 'TResult2, ^Awaiter, 'TOverall> :
            task: ^TaskLike * continuation: ('TResult1 -> ResumableTSC<'TResult2>) -> ResumableTSC<'TResult2>
                when ^TaskLike: (member GetAwaiter: unit -> ^Awaiter)
                and ^Awaiter :> ICriticalNotifyCompletion
                and ^Awaiter: (member get_IsCompleted: unit -> bool)
                and ^Awaiter: (member GetResult: unit -> 'TResult1)

/// <summary>
/// Contains low priority extension methods for the main builder class for the <see cref="taskSeq" /> computation expression.
/// The <see cref="LowPriority" />, <see cref="MediumPriority" /> and <see cref="HighPriority" /> modules are not meant to be
/// accessed directly from user code. They solely serve to disambiguate overload resolution inside the <see cref="taskSeq" /> computation expression.
/// </summary>
[<AutoOpen>]
module MediumPriority =
    type TaskSeqBuilder with

        member inline Using: disp: 'a * body: ('a -> ResumableTSC<'T>) -> ResumableTSC<'T> when 'a :> IDisposable
        member inline For: sequence: seq<'TElement> * body: ('TElement -> ResumableTSC<'T>) -> ResumableTSC<'T>
        member inline YieldFrom: source: seq<'T> -> ResumableTSC<'T>
        member inline For: source: #taskSeq<'TElement> * body: ('TElement -> ResumableTSC<'T>) -> ResumableTSC<'T>
        member inline YieldFrom: source: taskSeq<'T> -> ResumableTSC<'T>

/// <summary>
/// Contains low priority extension methods for the main builder class for the <see cref="taskSeq" /> computation expression.
/// The <see cref="LowPriority" />, <see cref="MediumPriority" /> and <see cref="HighPriority" /> modules are not meant to be
/// accessed directly from user code. They solely serve to disambiguate overload resolution inside the <see cref="taskSeq" /> computation expression.
/// </summary>
[<AutoOpen>]
module HighPriority =
    type TaskSeqBuilder with

        member inline Bind: task: Task<'TResult1> * continuation: ('TResult1 -> ResumableTSC<'T>) -> ResumableTSC<'T>
