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
    /// while another's marks are still unread breaks the invariant, and the
    /// first list's DrawWorld reports it (debug builds). The break is sticky:
    /// once another list has marked, the first list stays broken until its own
    /// Clear or DrawWorld, however many marks it makes afterwards and whether
    /// or not the other list has drawn or cleared in between. A list that
    /// marks and is then abandoned without drawing gives way to the next one
    /// that marks. Main thread only, like the lists themselves.
    ///
    /// The mechanism is one process-wide count of cover marks: a list
    /// remembers the count after its own last mark, and any mark it did not
    /// make moves the count past what it remembers.
    /// </remarks>
    internal struct CoverOwner
    {
        /// <summary>Cover marks made by any list, ever.</summary>
        private static long s_total;

        /// <summary>This list's cover marks since its last Clear or DrawWorld.</summary>
        private int _marks;

        /// <summary><see cref="s_total"/> right after this list's last mark.</summary>
        private long _afterMine;

        /// <summary>Another list marked while this one's marks were unread; set until Release.</summary>
        private bool _broken;

        public readonly int Marks => _marks;

        /// <summary>This list has just marked a cover tile.</summary>
        public void Marked()
        {
            if (_marks > 0 && s_total != _afterMine)
            {
                _broken = true;
            }

            s_total++;
            _afterMine = s_total;
            _marks++;
        }

        /// <summary>
        /// True when this list's cover marks can still be read: it has none,
        /// or no other list has marked since its first one.
        /// </summary>
        public readonly bool Intact => _marks == 0 || (!_broken && s_total == _afterMine);

        /// <summary>This list's marks are void (Clear, or DrawWorld has read them).</summary>
        public void Release()
        {
            _marks = 0;
            _broken = false;
        }
    }
}
