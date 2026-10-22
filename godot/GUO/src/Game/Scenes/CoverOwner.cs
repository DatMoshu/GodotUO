// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Game.Scenes
{
    /// <summary>
    /// Which RenderLists' cover marks are the live ones. A land tile holds one
    /// <see cref="CoverStamp"/>, so between a list's first cover mark and the
    /// DrawWorld that reads them, no other list may mark cover land; this
    /// records who marked last and lets DrawWorld check that it was itself.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): the HashSet per list that the stamp replaced did
    /// not have this limit, so the limit is stated and checked rather than
    /// assumed. The client has several RenderLists at once (the world scene's,
    /// the UI manager's two, and child lists that UI controls make and draw
    /// without ever clearing), but only the world scene's queues cover land:
    /// a list that marked nothing is always intact, so UI lists clear and draw
    /// between the world's gather and its draw freely, and a child list drawn
    /// without a Clear is unaffected. Only a second list that marks cover land
    /// while another's marks are still unread breaks the invariant, and that
    /// list's DrawWorld reports it (debug builds). A list that marks and is
    /// then abandoned without drawing gives way to the next one that marks.
    /// Main thread only, like the lists themselves.
    /// </remarks>
    internal struct CoverOwner
    {
        /// <summary>The list that marked cover land most recently, while its marks may still be unread.</summary>
        private static object s_latest;

        /// <summary>This list's cover marks since its last Clear or DrawWorld.</summary>
        private int _marks;

        public readonly int Marks => _marks;

        /// <summary>This list has just marked a cover tile.</summary>
        public void Marked(object list)
        {
            s_latest = list;
            _marks++;
        }

        /// <summary>
        /// True when this list's cover marks can still be read: it has none,
        /// or nobody marked since it did.
        /// </summary>
        public readonly bool Intact(object list) => _marks == 0 || ReferenceEquals(s_latest, list);

        /// <summary>This list's marks are void (Clear, or DrawWorld has read them).</summary>
        public void Release(object list)
        {
            _marks = 0;

            if (ReferenceEquals(s_latest, list))
            {
                s_latest = null;
            }
        }
    }
}
