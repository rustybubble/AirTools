using System.Linq;
using AirTools.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    // measure-edges: "measure the length of the top roof from end to end" on gt-lcc-canopy (planes, no objects). The backend
    // (server/structure_measure.py) sends measure_edges with the segment in the structure file's frame; the headset tapes it.
    public class MeasureEdgesTests
    {
        // As sent by the patched server (:8005) for gt-lcc-canopy: the top roof end to end, 8.35 m in scene metres.
        const string Canopy = @"{""label"":""Top roof length"",""request_id"":""me-0f4ca52f"",""what"":""length"",
            ""segments"":[{""a"":[-6.6821,-1.0111,-4.8895],""b"":[-1.1592,-1.956,1.3028]}]}";

        [Test]
        public void ParsesTheServersSegmentIntoStructurePoints()
        {
            var a = MeasureEdgesActions.Parse(JObject.Parse(Canopy));
            Assert.AreEqual("Top roof length", a.Label);
            Assert.AreEqual("me-0f4ca52f", a.RequestId);
            Assert.AreEqual("length", a.What);
            Assert.AreEqual(1, a.Count);
            // glTF → Unity: X flips, like the structure layer and the context's pointer.
            Assert.AreEqual(6.6821f, a.A[0].x, 1e-4f);
            Assert.AreEqual(-1.0111f, a.A[0].y, 1e-4f);
            Assert.AreEqual(-4.8895f, a.A[0].z, 1e-4f);
            Assert.AreEqual(1.1592f, a.B[0].x, 1e-4f);
            float d = Vector3.Distance(a.A[0], a.B[0]);
            Assert.AreEqual(8.351f, d, 1e-3f, "the roof end to end, scene metres; a flip keeps the length");
        }

        [Test]
        public void SkipsBadSegmentsAndTakesEdgeIds()
        {
            var a = MeasureEdgesActions.Parse(JObject.Parse(@"{""segments"":[{""a"":[0,0],""b"":[1,1,1]},{""a"":[0,0,0],""b"":[""1"",""2"",""2""]},""junk""],""edge_ids"":[""e3"",null,""e7""]}"));
            Assert.AreEqual(1, a.Count, "the 2-number point is skipped; numbers as strings are fine");
            Assert.AreEqual(3f, Vector3.Distance(a.A[0], a.B[0]), 1e-5f);
            CollectionAssert.AreEqual(new[] { "e3", "e7" }, a.EdgeIds);
            Assert.AreEqual("Distance", a.Label, "no label: a plain distance");
            Assert.IsNull(MeasureEdgesActions.Parse(JObject.Parse(@"{""label"":""Roof length"",""segments"":[]}")), "nothing to tape");
            Assert.IsNull(MeasureEdgesActions.Parse(null));
        }

        [Test]
        public void TheHandlerIsRegistered()
        {
            var names = typeof(MeasureEdgesActions).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(m => m.GetCustomAttributes(typeof(AgentActionAttribute), false).Cast<AgentActionAttribute>()).Select(x => x.Name).ToList();
            CollectionAssert.Contains(names, "measure_edges");
        }
    }
}
