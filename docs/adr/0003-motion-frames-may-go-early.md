# 3. A motion frame may go up to a quarter of an interval early

Date: 2026-11-27. Status: accepted. No game recording has shown the fault this
fixes. Torque Drift through `bepinex-ogs-ts` with the change held 60.0 Hz,
without a recording from before it to compare.

## Context

A sender sends at most one motion frame per host tick, and waited for the tick
at or after the frame's due time. A host running close to the motion rate falls
behind the schedule a little each tick. Once more than an interval behind, the
sender restarts the schedule from that tick. The next tick is then due almost
exactly one interval later, so a tick a fraction of a millisecond early sends
nothing, and the gap doubles.

In a simulation at 59.94 ticks a second with 0.5 ms of jitter, the sender
managed 51 Hz with 60 declared.
`implementation-udp.md` § Send cadence requires the declared rate to be the
achieved mean.

A Hot Wheels Unleashed recording through `ue4ss-ogs-ts` that averaged 54 to
57 Hz prompted the work. This change alone didn't remove its gaps. They came
from that mod's own sampling gate, which its ADR 0004 covers.

## Decision

Send on a tick that arrives up to a quarter of an interval before the due time.
The next due time still advances from the due time, so early sends don't raise
the long-run rate.

In the simulation, at host rates from 45 to 240 ticks a second with 0.5 ms and
2 ms of jitter, a quarter was the smallest tolerance tried that kept every case
within 2% of the lower of the host rate and 60 Hz. An eighth lost frames at
59.94 with 2 ms. The long-run rate never passed 60 Hz.

## Consequences

- The gap between two motion frames can be up to a quarter of an interval
  shorter than the interval.
- A host slower than the motion rate still sends once per tick.
- `ogs-ts-unreal` and the senders built on it schedule the same way.
