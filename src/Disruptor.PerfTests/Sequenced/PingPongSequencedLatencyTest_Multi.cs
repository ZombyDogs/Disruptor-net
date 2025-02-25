﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Disruptor.Dsl;
using Disruptor.PerfTests.Support;
using Disruptor.Processing;
using HdrHistogram;

namespace Disruptor.PerfTests.Sequenced;

public class PingPongSequencedLatencyTest_Multi : ILatencyTest
{
    private const int _bufferSize = 1024;
    private const long _iterations = 100 * 1000 * 30;
    private const long _pauseNanos = 1000;

    ///////////////////////////////////////////////////////////////////////////////////////////////

    private readonly Pinger _pinger;
    private readonly IEventProcessor<PerfEvent> _pingProcessor;

    private readonly Ponger _ponger;
    private readonly IEventProcessor<PerfEvent> _pongProcessor;

    public PingPongSequencedLatencyTest_Multi()
    {
        var pingBuffer = RingBuffer<PerfEvent>.CreateMultiProducer(PerfEvent.EventFactory, _bufferSize, new BlockingWaitStrategy());
        var pongBuffer = RingBuffer<PerfEvent>.CreateMultiProducer(PerfEvent.EventFactory, _bufferSize, new BlockingWaitStrategy());

        var pingBarrier = pingBuffer.NewBarrier();
        var pongBarrier = pongBuffer.NewBarrier();

        _pinger = new Pinger(pingBuffer, _iterations, _pauseNanos);
        _ponger = new Ponger(pongBuffer);

        _pingProcessor = EventProcessorFactory.Create(pongBuffer,pongBarrier, _pinger);
        _pongProcessor = EventProcessorFactory.Create(pingBuffer,pingBarrier, _ponger);

        pingBuffer.AddGatingSequences(_pongProcessor.Sequence);
        pongBuffer.AddGatingSequences(_pingProcessor.Sequence);
    }

    public int RequiredProcessorCount => 2;

    ///////////////////////////////////////////////////////////////////////////////////////////////

    public void Run(Stopwatch stopwatch, HistogramBase histogram)
    {
        var globalSignal = new CountdownEvent(3);
        var signal = new ManualResetEvent(false);
        _pinger.Reset(globalSignal, signal, histogram);
        _ponger.Reset(globalSignal);

        var processorTask1 = _pongProcessor.Start();
        var processorTask2 = _pingProcessor.Start();
        _pongProcessor.WaitUntilStarted(TimeSpan.FromSeconds(5));
        _pingProcessor.WaitUntilStarted(TimeSpan.FromSeconds(5));

        globalSignal.Signal();
        globalSignal.Wait();
        stopwatch.Start();
        // running here
        signal.WaitOne();
        stopwatch.Stop();

        _pingProcessor.Halt();
        _pongProcessor.Halt();
        Task.WaitAll(processorTask1, processorTask2);

        //Console.WriteLine(MultiProducerSequencer.FailureCount);
    }

    private class Pinger : IEventHandler<PerfEvent>
    {
        private readonly RingBuffer<PerfEvent> _buffer;
        private readonly long _maxEvents;
        private readonly long _pauseTimeNs;
        private readonly long _pauseTimeTicks;
        private HistogramBase _histogram;
        private long _t0;
        private long _counter;
        private CountdownEvent _globalSignal;
        private ManualResetEvent _signal;

        public Pinger(RingBuffer<PerfEvent> buffer, long maxEvents, long pauseTimeNs)
        {
            _buffer = buffer;
            _maxEvents = maxEvents;
            _pauseTimeNs = pauseTimeNs;
            _pauseTimeTicks = LatencyTestSession.ConvertNanoToStopwatchTicks(pauseTimeNs);
        }

        public void OnEvent(PerfEvent data, long sequence, bool endOfBatch)
        {
            var t1 = Stopwatch.GetTimestamp();

            _histogram.RecordValueWithExpectedInterval(LatencyTestSession.ConvertStopwatchTicksToNano(t1 - _t0), _pauseTimeNs);

            if (data.Value < _maxEvents)
            {
                while (_pauseTimeTicks > (Stopwatch.GetTimestamp() - t1))
                {
                    Thread.Yield();
                }

                Send();
            }
            else
            {
                _signal.Set();
            }
        }

        private void Send()
        {
            _t0 = Stopwatch.GetTimestamp();
            var next = _buffer.Next();
            _buffer[next].Value = _counter;
            _buffer.Publish(next);

            _counter++;
        }

        public void OnStart()
        {
            _globalSignal.Signal();
            _globalSignal.Wait();

            Thread.Sleep(1000);

            Send();
        }

        public void OnShutdown()
        {
        }

        public void Reset(CountdownEvent globalSignal, ManualResetEvent signal, HistogramBase histogram)
        {
            _histogram = histogram;
            _globalSignal = globalSignal;
            _signal = signal;

            _counter = 0;
        }
    }

    private class Ponger : IEventHandler<PerfEvent>
    {
        private readonly RingBuffer<PerfEvent> _buffer;
        private CountdownEvent _globalSignal;

        public Ponger(RingBuffer<PerfEvent> buffer)
        {
            _buffer = buffer;
        }

        public void OnEvent(PerfEvent data, long sequence, bool endOfBatch)
        {
            var next = _buffer.Next();
            _buffer[next].Value = data.Value;
            _buffer.Publish(next);
        }

        public void OnStart()
        {
            _globalSignal.Signal();
            _globalSignal.Wait();
        }

        public void OnShutdown()
        {
        }

        public void Reset(CountdownEvent globalSignal)
        {
            _globalSignal = globalSignal;
        }
    }
}
