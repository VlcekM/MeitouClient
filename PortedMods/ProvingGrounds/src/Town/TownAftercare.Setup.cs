using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Task 8.4a (TW §2.9 "Setup at staging", "During the fight"): the host's buildings, beds and orders for Begin and Standby, and the TEST ONLY
// pg town care listing. Engine calls: the E1 list in TownAftercare.cs.

sealed partial class TownAftercare
{
    /// <summary>The building record of each bed fixture listed by <see cref="Host.BedsOf"/> (the hospital it stands in), so a bed read needs no
    /// building search. Cleared with the world (this object is per world).</summary>
    readonly Dictionary<string, string> bedBuildingRecord = [];

    sealed partial class Host
    {
        // ---------- orders ----------
        public bool Hold(CharacterId c, HoldOwner holdOwner) => owner.holds.Hold(W, c, holdOwner);
        public void ReleaseHold(CharacterId c, HoldOwner holdOwner) => owner.holds.Release(W, c, holdOwner);
        public byte MoveSpeed(CharacterId c) => PgEngine.RunSpeedOf(W, c);
        public void RestoreMoveSpeed(CharacterId c, byte speed) => PgEngine.SetRunSpeedMode(W, c, speed);
        public void DropCarried(CharacterId c) => PgEngine.LetGo(W, c);
        public void Run(CharacterId c) => PgEngine.SetRunSpeed(W, c);
        public void MoveTo(CharacterId c, Vector3 point) => PgEngine.MoveTo(W, c, point);

        // ---------- buildings and beds ----------
        public PlacedBuilding? Resolve(ArenaBuildingRef held) =>
            ArenaIdentity.Resolve(W, owner.data, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;

        public IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max)
        {
            var production = PgEngine.Production(W);
            var found = PgEngine.BuildingsNear(W, owner.data, origin, radius, null, max);
            var list = new List<PlacedBuilding>(found.Count);
            foreach (var b in found) list.Add(new PlacedBuilding(ArenaIdentity.From(b, production), Towns.Key(b.Town)));
            return list;
        }

        public IReadOnlyList<string> BedsOf(ArenaBuilding building)
        {
            var ids = building.Source == ArenaBuildingSource.Town ? Beds.OfBuilding(W, building.Key) : [];
            foreach (var id in ids) owner.bedBuildingRecord[id] = building.RecordId;
            return ids;
        }

        public AftercareBed<CharacterId>? Bed(string id)
        {
            if (Beds.Find(W, id) is not { } f) return null;
            string record = owner.bedBuildingRecord.TryGetValue(id, out var r) ? r : "";
            return new AftercareBed<CharacterId>(f.Id, record, f.Bed.Function, f.Occupant, Beds.Occupied(W, f), Beds.LiesIn(W, f.Occupant, f),
                f.Position);
        }
    }

    // ---------- TEST ONLY: pg town care ----------

    /// <summary>TEST ONLY <c>pg town care</c> (task 8.4a): the aftercare's state as JSON fields (appended to the PG-TOWN line): the care id and status,
    /// the standby point and side, the arena and hospital, every medic (slot, id, name, directed, the command's action, its line spot and distance,
    /// held and the hold's owners, reserved), the patients (winner, managed), the hospital's beds (function and kind, record, power output, occupant,
    /// held, lying in it), the bed registry, every hold of the world's registry with its owners, and the four tables' members. Port only.</summary>
    internal string CareJson(World world, string failuresJson = "{}")
    {
        var h = H(world);
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string F(float f) => TestJson.G(f);   // task 8.8: NaN / infinity -> null
        static string V(Vector3 v) => "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]";
        static string Id(CharacterId c) => PGLog.Quote(CharacterIdText.Format(c));
        string Ref(ArenaBuildingRef? r) => r is { } x ? PGLog.Quote(ArenaIdentity.KeyText(x)) : "null";
        var sb = new StringBuilder(2048);
        sb.Append(",\"care\":{\"controlAvailable\":").Append(B(logic.ControlAvailable()))
          .Append(",\"failures\":").Append(failuresJson)   // task 8.4c quality review: per aftercare step
          .Append(",\"careId\":").Append(N(logic.CareId))
          .Append(",\"status\":").Append(PGLog.Quote(logic.GetStatus()))
          .Append(",\"standby\":").Append(V(logic.StandbyPoint))
          .Append(",\"side\":").Append(V(logic.StandbySide))
          .Append(",\"arena\":").Append(Ref(logic.ArenaSite))
          .Append(",\"arenaPos\":").Append(logic.ArenaSite is { } site && h.Resolve(site) is { } ap ? V(ap.Building.Position) : "null")
          .Append(",\"hospital\":").Append(Ref(logic.ReturnHospital));
        var medics = logic.Medics;
        sb.Append(",\"medics\":[");
        for (int i = 0; i < medics.Length; i++)
        {
            var m = medics[i];
            float offset = ((float)i - ((float)medics.Length - 1.0f) * .5f) * 16.0f;
            Vector3 spot = logic.StandbyPoint + logic.StandbySide * offset;
            var pos = PgEngine.Position(world, m.Actor);
            float dist = pos is { } p ? MathF.Sqrt((p.X - spot.X) * (p.X - spot.X) + (p.Z - spot.Z) * (p.Z - spot.Z)) : -1f;
            if (i > 0) sb.Append(',');
            sb.Append("{\"slot\":").Append(N(i + 1)).Append(",\"id\":").Append(Id(m.Actor))
              .Append(",\"record\":").Append(PGLog.Quote(PgEngine.RecordId(world, m.Actor) ?? ""))
              .Append(",\"name\":").Append(PGLog.Quote(PgEngine.Name(world, m.Actor) ?? ""))
              .Append(",\"directed\":").Append(B(m.Directed))
              .Append(",\"action\":").Append(PGLog.Quote(m.Command.Action.ToString()))
              .Append(",\"spot\":").Append(V(spot))
              .Append(",\"pos\":").Append(pos is { } pp ? V(pp) : "null")
              .Append(",\"dist\":").Append(F(dist))
              .Append(",\"held\":").Append(B(holds.IsHeld(m.Actor)))
              .Append(",\"owners\":").Append(PGLog.Quote(holds.OwnersOf(m.Actor).ToString()))
              .Append(",\"engaged\":").Append(B(PgEngine.IsEngaged(world, m.Actor)))
              .Append(",\"reserved\":").Append(B(logic.MedicReserved(m.Actor)))
              .Append(",\"speed\":").Append(N(PgEngine.RunSpeedOf(world, m.Actor)))
              // task 8.4b: the treatment
              .Append(",\"kind\":").Append(PGLog.Quote(m.LastKind.ToString()))
              .Append(",\"patient\":").Append(m.Patient.IsNone ? "null" : Id(m.Patient))
              .Append(",\"bed\":").Append(m.Bed is { } mb ? PGLog.Quote(mb) : "null")
              .Append(",\"lastOrder\":").Append(PGLog.Quote(m.LastOrder ?? ""))
              .Append(",\"carrying\":").Append(PgEngine.CarryingOf(world, m.Actor) is { IsNone: false } cc ? Id(cc) : "null")
              .Append(",\"task\":").Append(PGLog.Quote(PgEngine.TaskText(world, m.Actor)))
              .Append('}');
        }
        sb.Append("],\"patients\":[");
        var patients = logic.Patients;
        for (int i = 0; i < patients.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"slot\":").Append(N(i + 1)).Append(",\"id\":").Append(Id(patients[i].Actor))
              .Append(",\"winner\":").Append(B(patients[i].Winner))
              .Append(",\"managed\":").Append(B(logic.IsManaged(h, patients[i].Actor)))
              // task 8.4b: the treatment
              .Append(",\"directed\":").Append(B(patients[i].Directed))
              .Append(",\"reserved\":").Append(B(logic.PatientReserved(patients[i].Actor)))
              .Append(",\"owners\":").Append(PGLog.Quote(holds.OwnersOf(patients[i].Actor).ToString()))
              .Append(",\"early\":").Append(B(patients[i].EarlyAdmitted))
              .Append(",\"sweep\":").Append(B(patients[i].SweepAdmitted))
              .Append(",\"ko\":").Append(B(PgEngine.IsUnconscious(world, patients[i].Actor)))
              .Append(",\"dead\":").Append(B(PgEngine.IsDead(world, patients[i].Actor)))
              .Append(",\"carried\":").Append(B(PgEngine.IsCarried(world, patients[i].Actor)))
              .Append(",\"inBed\":").Append(Beds.InWhat(world, patients[i].Actor) is { } inw ? PGLog.Quote(inw) : "null")
              .Append(",\"task\":").Append(PGLog.Quote(PgEngine.TaskText(world, patients[i].Actor)))
              .Append(",\"aid\":").Append(F(PgEngine.AidNeed(world, patients[i].Actor, false)))
              .Append(",\"robot\":").Append(B(PgEngine.IsRobot(world, patients[i].Actor)))
              .Append('}');
        }
        sb.Append("],\"beds\":[");
        var beds = logic.Beds;
        for (int i = 0; i < beds.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = Beds.Find(world, beds[i]);
            var info = h.Bed(beds[i]);
            int? power = f is null ? null : Beds.PowerOutput(data, f);
            sb.Append("{\"id\":").Append(PGLog.Quote(beds[i]));
            if (f is not null && info is { } b)
            {
                sb.Append(",\"record\":").Append(PGLog.Quote(f.RecordId))
                  .Append(",\"building\":").Append(PGLog.Quote(b.BuildingRecord ?? ""))   // task 8.3 quality review: town-ambient (7)
                  .Append(",\"function\":").Append(N(b.Function))
                  .Append(",\"kind\":").Append(PGLog.Quote(b.Function == TownAftercareLogic<CharacterId>.SkeletonBedFunction ? "skeleton" : "normal"))
                  .Append(",\"power\":").Append(power is { } pw ? N(pw) : "null")
                  .Append(",\"powered\":").Append(B(Beds.Powered(world, data, f)))   // task 8.4b: BedSystem.HasPower's tests
                  .Append(",\"y\":").Append(F(f.Position.Y))
                  .Append(",\"occupant\":").Append(b.Occupant.IsNone ? "null" : Id(b.Occupant))
                  .Append(",\"occupied\":").Append(B(b.Occupied))
                  .Append(",\"inBed\":").Append(B(b.OccupantInBed));
            }
            sb.Append('}');
        }
        sb.Append("],\"bedRegistry\":[");
        var reg = logic.BedOccupants;
        for (int i = 0; i < reg.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Id(reg[i].Actor)).Append(",\"bed\":").Append(PGLog.Quote(reg[i].Bed ?? ""))
              .Append(",\"building\":").Append(reg[i].Bed is { } rb && h.Bed(rb) is { } rbi ? PGLog.Quote(rbi.BuildingRecord ?? "") : "null").Append('}');   // task 8.3 quality review
        }
        sb.Append("],\"holds\":[");
        var registry = holds.Registry;
        for (int i = 0; i < registry.Count; i++)
        {
            var (id, owners) = registry.At(i);
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Id(id)).Append(",\"owners\":").Append(PGLog.Quote(owners.ToString()))
              .Append(",\"engaged\":").Append(B(PgEngine.IsEngaged(world, id))).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
