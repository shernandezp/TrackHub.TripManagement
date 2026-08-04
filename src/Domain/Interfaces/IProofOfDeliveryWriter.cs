// Copyright (c) 2026 Sergio Hernandez. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

namespace TrackHub.TripManagement.Domain.Interfaces;

/// <summary>
/// Proof-of-delivery capture. Idempotent on the unique <c>(TripStopId, ClientEventId)</c> index,
/// so a retried submission returns the existing record instead of creating a second one
/// (acceptance 15).
/// </summary>
public interface IProofOfDeliveryWriter
{
    /// <summary>
    /// Records a POD and links its documents. Document ids are validated by the caller against
    /// the account and <c>ScanStatus = Clean</c> BEFORE this is reached — the store links, it does
    /// not authorize.
    /// <para>
    /// <c>Created</c> is false when the unique <c>(TripStopId, ClientEventId)</c> index matched an
    /// existing row. Callers MUST check it before applying side effects: the handler used to mark
    /// the stop's deliveries <c>Delivered</c> unconditionally, so a replayed POD — exactly what
    /// spec 10's offline outbox produces — silently reverted a later, genuine <c>Rejected</c>
    /// outcome. Idempotent means no second row AND no repeated side effect.
    /// </para>
    /// </summary>
    Task<(ProofOfDeliveryVm ProofOfDelivery, bool Created)> RecordAsync(
        Guid accountId,
        Guid tripId,
        ProofOfDeliveryDto proofOfDelivery,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether this exact submission was already recorded — the <c>(TripStopId, ClientEventId)</c>
    /// key acceptance 15 rests on.
    /// <para>
    /// Exposed so the handler can settle idempotency BEFORE its trip-state guard. A POD is delivery
    /// evidence and the trip closes right behind it — auto-completion fires the moment the last stop
    /// does (§5.2) — so a device re-sending a POD the server already has must be told yes, not
    /// <c>TRIP_ALREADY_TERMINAL</c>, which spec 10's outbox can only retry forever.
    /// </para>
    /// </summary>
    Task<bool> HasAsync(Guid accountId, Guid tripStopId, Guid clientEventId, CancellationToken cancellationToken);
}
