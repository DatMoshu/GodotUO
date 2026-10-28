// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
namespace GUO.Labs;
internal static partial class LabsRegistration
{
    static partial void RegisterPaging(Context context)
    {
        context.Command("paging", args => Paging.PagingLab.Command(context.World, args));
    }
}
#endif
