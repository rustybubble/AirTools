using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    // The shapes of backend docs/api.md §4 (POST /job/run, GET /job/run/{run_id}?after=n, /coach/*) and §5 (job_*,
    // coach_* actions), field names as sent. Examples: backend tests/test_runjob.py, tests/test_coach.py.

    /// job_started {run_id, steps: [names], kind?, title?}. autonomy: kind "replace" (one sentence → the whole replace,
    /// backend server/replace_job.py) carries the rail's title ("Replace the dishwasher"); "do the whole job" sends neither.
    public class JobStartedArgs
    {
        public string run_id;
        public List<string> steps = new List<string>();
        public string kind;
        public string title;
    }

    /// job_step {i, name, status: done | skipped | stopped (pending in the poll's steps[]), spoken, seconds, cost_usd}.
    public class JobStepArgs
    {
        public int i;
        public string name;
        public string status;
        public string spoken;
        public double? seconds;
        public double? cost_usd;
    }

    /// job_done {run_id, status: done | stopped, total_usd, after_rebates_usd, packet_url, cost_usd}; autonomy (kind
    /// "replace"): spoken (said aloud), summary (the chip: "Whirlpool WDP540HAMW in the dishwasher gap · an exact fit"),
    /// part_id, fits, models_ready.
    public class JobDoneArgs
    {
        public string run_id;
        public string status;
        public double? total_usd;
        public double? after_rebates_usd;
        public string packet_url;
        public double? cost_usd;
        public string kind;
        public string spoken;
        public string summary;
        public string part_id;
        public string fits;
        public int? models_ready;
    }

    /// GET /job/run/{run_id}?after=n → {run_id, status: running | done | stopped, steps[], actions[] (from n on), next}.
    public class JobPollBody
    {
        public string run_id;
        public string status;
        public List<JobStepArgs> steps = new List<JobStepArgs>();
        public List<AgentAction> actions = new List<AgentAction>();
        public int next;
    }

    /// One entry of coach_started.steps: {i, say, page}.
    public class CoachStepInfo
    {
        public int i;
        public string say;
        public int? page;
    }

    /// coach_started {coach_id, job, label, source: manual | template, steps: [{i, say, page}], label_note}.
    public class CoachStartedArgs
    {
        public string coach_id, job, label, source, label_note;
        public List<CoachStepInfo> steps = new List<CoachStepInfo>();
    }

    /// coach_step {coach_id, i, of, say, page, quote, pdf_url, tool: "drill" | null, checks: [questions]}.
    public class CoachStepArgs
    {
        public string coach_id;
        public int i;
        public int of;
        public string say;
        public int? page;
        public string quote, pdf_url, tool;
        public List<string> checks = new List<string>();
    }

    /// One of coach_check.results: the check plus the model's {answer: yes | no | cant_see, evidence, box (0–1 or null), ok}.
    public class CoachResult
    {
        public string id, question, expect, look_at, answer, evidence;
        public float[] box;
        public bool ok;
    }

    /// coach_check {coach_id, i, frame_id, verdict: passed | not_yet | look | stop, results, spoken}.
    public class CoachCheckArgs
    {
        public string coach_id;
        public int i;
        public string frame_id, verdict, spoken;
        public List<CoachResult> results = new List<CoachResult>();
    }

    /// coach_stop {coach_id, i, kind: outlet | switch, box, drill_px}.
    public class CoachStopArgs
    {
        public string coach_id;
        public int i;
        public string kind;
        public float[] box;
        public float[] drill_px;
    }

    /// coach_done {coach_id, checked, overridden}.
    public class CoachDoneArgs
    {
        public string coach_id;
        public int @checked;
        public int overridden;
    }

    /// JSON → the shapes above. Lenient: a missing or mistyped field reads as its default (null for strings and boxes),
    /// never an exception, so one odd field can't drop a whole step.
    public static class GrokRailPayloads
    {
        public static JobStartedArgs JobStarted(JObject a)
        {
            if (a == null) return null;
            return new JobStartedArgs { run_id = Str(a, "run_id"), steps = Strings(a["steps"]), kind = Str(a, "kind"), title = Str(a, "title") };
        }

        public static JobStepArgs JobStep(JObject a)
        {
            if (a == null) return null;
            return new JobStepArgs
            {
                i = Int(a, "i") ?? -1,
                name = Str(a, "name"),
                status = Str(a, "status"),
                spoken = Str(a, "spoken"),
                seconds = Num(a, "seconds"),
                cost_usd = Num(a, "cost_usd"),
            };
        }

        public static JobDoneArgs JobDone(JObject a)
        {
            if (a == null) return null;
            return new JobDoneArgs
            {
                run_id = Str(a, "run_id"),
                status = Str(a, "status"),
                total_usd = Num(a, "total_usd"),
                after_rebates_usd = Num(a, "after_rebates_usd"),
                packet_url = Str(a, "packet_url"),
                cost_usd = Num(a, "cost_usd"),
                kind = Str(a, "kind"),              // autonomy
                spoken = Str(a, "spoken"),
                summary = Str(a, "summary"),
                part_id = Str(a, "part_id"),
                fits = Str(a, "fits"),
                models_ready = Int(a, "models_ready"),
            };
        }

        /// The GET /job/run body; null if it isn't JSON.
        public static JobPollBody Poll(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            JObject o;
            try { o = JObject.Parse(json); }
            catch (JsonException) { return null; }
            var body = new JobPollBody { run_id = Str(o, "run_id"), status = Str(o, "status"), next = Int(o, "next") ?? 0 };
            if (o["steps"] is JArray steps)
                foreach (var s in steps)
                    if (s is JObject so) body.steps.Add(JobStep(so));
            if (o["actions"] is JArray actions)
                foreach (var t in actions)
                    if (t is JObject ao && Str(ao, "name") is string name)
                        body.actions.Add(new AgentAction { name = name, args = ao["args"] as JObject ?? new JObject() });
            return body;
        }

        public static CoachStartedArgs CoachStarted(JObject a)
        {
            if (a == null) return null;
            var r = new CoachStartedArgs
            {
                coach_id = Str(a, "coach_id"), job = Str(a, "job"), label = Str(a, "label"),
                source = Str(a, "source"), label_note = Str(a, "label_note"),
            };
            if (a["steps"] is JArray steps)
                foreach (var s in steps)
                    if (s is JObject so) r.steps.Add(new CoachStepInfo { i = Int(so, "i") ?? r.steps.Count, say = Str(so, "say"), page = Int(so, "page") });
            return r;
        }

        public static CoachStepArgs CoachStep(JObject a)
        {
            if (a == null) return null;
            var r = new CoachStepArgs
            {
                coach_id = Str(a, "coach_id"), i = Int(a, "i") ?? 0, of = Int(a, "of") ?? 0, say = Str(a, "say"),
                page = Int(a, "page"), quote = Str(a, "quote"), pdf_url = Str(a, "pdf_url"), tool = Str(a, "tool"),
            };
            // Questions as strings (coach_step); GET /coach/{id} keeps them as {id, question, expect, look_at}.
            if (a["checks"] is JArray checks)
                foreach (var c in checks)
                {
                    string q = c.Type == JTokenType.String ? (string)c : c is JObject co ? Str(co, "question") : null;
                    if (!string.IsNullOrWhiteSpace(q)) r.checks.Add(q);
                }
            return r;
        }

        public static CoachCheckArgs CoachCheck(JObject a)
        {
            if (a == null) return null;
            var r = new CoachCheckArgs
            {
                coach_id = Str(a, "coach_id"), i = Int(a, "i") ?? 0, frame_id = Str(a, "frame_id"),
                verdict = Str(a, "verdict"), spoken = Str(a, "spoken"),
            };
            if (a["results"] is JArray results)
                foreach (var t in results)
                    if (t is JObject o)
                        r.results.Add(new CoachResult
                        {
                            id = Str(o, "id"), question = Str(o, "question"), expect = Str(o, "expect"), look_at = Str(o, "look_at"),
                            answer = Str(o, "answer"), evidence = Str(o, "evidence"), box = Box(o["box"]),
                            ok = o["ok"]?.Type == JTokenType.Boolean ? (bool)o["ok"] : Str(o, "answer") == Str(o, "expect") && Str(o, "answer") != null,
                        });
            return r;
        }

        public static CoachStopArgs CoachStop(JObject a)
        {
            if (a == null) return null;
            return new CoachStopArgs
            {
                coach_id = Str(a, "coach_id"), i = Int(a, "i") ?? 0, kind = Str(a, "kind"),
                box = Box(a["box"]), drill_px = Point(a["drill_px"]),
            };
        }

        public static CoachDoneArgs CoachDone(JObject a)
        {
            if (a == null) return null;
            return new CoachDoneArgs { coach_id = Str(a, "coach_id"), @checked = Int(a, "checked") ?? 0, overridden = Int(a, "overridden") ?? 0 };
        }

        // ---------------- helpers ----------------

        public static string Str(JObject o, string key)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        public static int? Int(JObject o, string key)
        {
            var t = o?[key];
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer: return (int)(long)t;
                case JTokenType.Float: return (int)Math.Round((double)t);
                case JTokenType.String:
                    return int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
                default: return null;
            }
        }

        public static double? Num(JObject o, string key)
        {
            var t = o?[key];
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return (double)t;
                case JTokenType.String:
                    return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                default: return null;
            }
        }

        public static List<string> Strings(JToken t)
        {
            var list = new List<string>();
            if (t is JArray a)
                foreach (var x in a)
                    if (x != null && x.Type == JTokenType.String) list.Add((string)x);
            return list;
        }

        /// [x0, y0, x1, y1] (0–1, top-left origin) or null.
        public static float[] Box(JToken t) => Floats(t, 4);

        /// [x, y] (0–1) or null.
        public static float[] Point(JToken t) => Floats(t, 2);

        static float[] Floats(JToken t, int n)
        {
            if (!(t is JArray a) || a.Count < n) return null;
            var r = new float[n];
            for (int k = 0; k < n; k++)
            {
                if (a[k].Type != JTokenType.Integer && a[k].Type != JTokenType.Float) return null;
                r[k] = (float)(double)a[k];
                if (float.IsNaN(r[k]) || float.IsInfinity(r[k])) return null;
            }
            return r;
        }
    }
}
