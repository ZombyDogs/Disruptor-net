﻿using System.Threading.Tasks;

namespace Disruptor.Dsl;

internal interface IConsumerInfo
{
    ISequence[] Sequences { get; }

    DependentSequenceGroup? DependentSequences { get; }

    bool IsEndOfChain { get; }

    void Start(TaskScheduler taskScheduler);

    void Halt();

    void MarkAsUsedInBarrier();

    bool IsRunning { get; }
}
