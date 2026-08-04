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

namespace TrackHub.TripManagement.Infrastructure.TripDB.Entities;

/// <summary>Persistence-side defaults for the spatial columns (spec 11 section 6.1).</summary>
public static class TripGeometryDefaults
{
    /// <summary>Mirrors the domain band so the column default and the validators cannot drift apart.</summary>
    public const int ArrivalRadiusMeters = TripGeometry.DefaultRadiusMeters;
    public const int CorridorMeters = 500;
    public const int Srid = 4326;
}
