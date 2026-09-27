using System.Collections.Generic;

namespace AirTools.Agent.Grok
{
    /// A dot on a progress rail. Done / Said are green (Said = on the user's word, install coach), Skipped grey,
    /// Stopped red, Active the current step, Pending not reached yet. Never colour alone: each has a glyph too.
    public enum DotStatus { Pending, Active, Done, Skipped, Stopped, Said }

    public sealed class RailDot
    {
        public string Name;
        public DotStatus Status;
        public string Spoken;
        public double? Seconds, CostUsd;
    }

    /// "Do the whole job" (backend F15, docs/api.md `job_started` / `job_step` / `job_done`): one dot per step name. Steps
    /// finish out of order (safety, rules and postcard run in parallel), so a job_step lands on its own index `i`.
    /// Pure: time is passed in.
    public sealed class JobRunModel
    {
        public string RunId { get; private set; }
        /// autonomy: "job" ("do the whole job") or "replace" (one sentence → the whole replace); the rail's heading.
        public string Kind { get; private set; } = "job";
        public string Title { get; private set; }
        public bool IsReplace => Kind == "replace";
        public readonly List<RailDot> Dots = new List<RailDot>();
        /// The last spoken step line (the rail's caption) and the step it came from.
        public string Caption { get; private set; } = "";
        public int CaptionStep { get; private set; } = -1;
        public JobDoneArgs Done { get; private set; }
        public bool Finished => Done != null;
        /// running | done | stopped | cancelled (switchclean: the person switched models mid-run).
        public string Status => Done?.status ?? "running";
        public double StartedAt { get; private set; }
        public double DoneAt { get; private set; } = -1;
        public int Updates { get; private set; }

        public static JobRunModel Start(JobStartedArgs a, double now)
        {
            if (a == null || string.IsNullOrEmpty(a.run_id)) return null;
            var m = new JobRunModel { RunId = a.run_id, StartedAt = now, Kind = string.IsNullOrEmpty(a.kind) ? "job" : a.kind, Title = a.title };
            foreach (var name in a.steps ?? new List<string>()) m.Dots.Add(new RailDot { Name = name ?? "", Status = DotStatus.Pending });
            return m;
        }

        public static DotStatus Parse(string status) => (status ?? "").ToLowerInvariant() switch
        {
            "done" => DotStatus.Done,
            "skipped" => DotStatus.Skipped,
            "stopped" => DotStatus.Stopped,
            _ => DotStatus.Pending,
        };

        /// The dot for a job_step: by `i` when it names the same step, else by name (a rail started from another source).
        public int IndexOf(JobStepArgs s)
        {
            if (s == null) return -1;
            if (s.i >= 0 && s.i < Dots.Count && (string.IsNullOrEmpty(s.name) || Dots[s.i].Name == s.name)) return s.i;
            for (int k = 0; k < Dots.Count; k++) if (Dots[k].Name == s.name) return k;
            return -1;
        }

        /// job_step: the dot turns green / grey / red and its spoken line becomes the caption. False for an unknown step.
        public bool Step(JobStepArgs s, double now)
        {
            int k = IndexOf(s);
            if (k < 0) return false;
            var d = Dots[k];
            d.Status = Parse(s.status);
            d.Spoken = s.spoken;
            d.Seconds = s.seconds;
            d.CostUsd = s.cost_usd;
            if (!string.IsNullOrEmpty(s.spoken)) { Caption = s.spoken; CaptionStep = k; }
            Updates++;
            return true;
        }

        /// The poll's steps[] is the server's truth: settle any dot whose job_step this client never saw.
        public int Reconcile(IList<JobStepArgs> steps)
        {
            if (steps == null) return 0;
            int changed = 0;
            foreach (var s in steps)
            {
                int k = IndexOf(s);
                if (k < 0) continue;
                var st = Parse(s.status);
                if (st == DotStatus.Pending || Dots[k].Status == st) continue;
                Dots[k].Status = st;
                if (Dots[k].Spoken == null) Dots[k].Spoken = s.spoken;
                changed++;
            }
            if (changed > 0) Updates++;
            return changed;
        }

        /// job_done: the summary chip; the rail fades after it (JobRailTiming).
        public bool Finish(JobDoneArgs d, double now)
        {
            if (d == null || (!string.IsNullOrEmpty(d.run_id) && d.run_id != RunId)) return false;
            Done = d;
            DoneAt = now;
            Updates++;
            return true;
        }

        /// switchclean: the person switched models mid-run: the run ends here as cancelled (`summary` says why). False
        /// when it had already finished.
        public bool Cancel(string summary, double now)
        {
            if (Finished) return false;
            return Finish(new JobDoneArgs { run_id = RunId, status = Cancelled, kind = Kind, summary = summary }, now);
        }

        /// switchclean: job_done's status for a run the headset stopped.
        public const string Cancelled = "cancelled";
        public bool IsCancelled => Status == Cancelled;

        /// Where the chain is (runjob.py): survey → part → safety + rules + postcard in parallel → packet → checkout.
        public static int Stage(string name, int index) => name switch
        {
            "survey" => 0,
            "part" => 1,
            "safety" => 2,
            "rules" => 2,
            "postcard" => 2,
            "packet" => 3,
            "checkout" => 4,
            _ => 10 + index,
        };

        /// The pending dots of the earliest unfinished stage are the ones working now (three at once mid-chain).
        public bool IsActive(int k)
        {
            if (Finished || k < 0 || k >= Dots.Count || Dots[k].Status != DotStatus.Pending) return false;
            int min = int.MaxValue;
            for (int j = 0; j < Dots.Count; j++)
                if (Dots[j].Status == DotStatus.Pending) min = System.Math.Min(min, Stage(Dots[j].Name, j));
            return Stage(Dots[k].Name, k) == min;
        }

        /// What the dot shows: its status, or Active while it's working.
        public DotStatus Display(int k) => IsActive(k) ? DotStatus.Active : k >= 0 && k < Dots.Count ? Dots[k].Status : DotStatus.Pending;

        public int Settled
        {
            get
            {
                int n = 0;
                foreach (var d in Dots) if (d.Status != DotStatus.Pending) n++;
                return n;
            }
        }
    }

    /// When the job rail shows: fully until `hold` seconds after job_done, then it fades out over `fade`.
    public static class JobRailTiming
    {
        public static float Alpha(double now, double doneAt, float hold, float fade)
        {
            if (doneAt < 0) return 1f;
            double t = now - doneAt - hold;
            if (t <= 0) return 1f;
            if (fade <= 0f || t >= fade) return 0f;
            return (float)(1.0 - t / fade);
        }
    }
}
