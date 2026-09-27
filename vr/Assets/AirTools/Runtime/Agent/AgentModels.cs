using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent
{
    /// One queued client action (backend docs/api.md §5). Args stay raw JSON; AgentActions reads what it needs.
    public class AgentAction
    {
        public string name;
        public JObject args = new JObject();

        public string Str(string key) => args?[key]?.Type == JTokenType.Null ? null : (string)args?[key];
        public int? Int(string key) => args?[key] == null || args[key].Type == JTokenType.Null ? (int?)null : (int)args[key];
        public float? Float(string key) => args?[key] == null || args[key].Type == JTokenType.Null ? (float?)null : (float)args[key];
        public override string ToString() => $"{name} {args?.ToString(Newtonsoft.Json.Formatting.None)}";
    }

    /// POST /agent/command response.
    public class AgentReply
    {
        public string reply;
        public List<AgentAction> actions = new List<AgentAction>();
        public string job_id;
        public string summary;
    }

    /// POST /voice/command response: the agent reply plus what was heard and the spoken reply.
    public class VoiceReply : AgentReply
    {
        public string transcript;
        public string audio_b64;
        public string audio_mime;
        public string tts_error;
    }

    /// POST /scene/ask response.
    public class SceneAnswer
    {
        public string answer;
        public string frame_id;
        /// [x0, y0, x1, y1], normalised 0–1, top-left origin; null when the model didn't locate anything.
        public float[] box;
        public string part_query;
    }
}
