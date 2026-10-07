using MPCore.Application.Querying;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// Turns what a transport received into MP Core's <see cref="PageRequest"/>, the same way on every
/// transport.
/// </summary>
/// <remarks>
/// It exists because the two transports express "the caller did not choose" differently: a REST query
/// string omits <c>page</c> entirely, while a proto <c>int32</c> is zero when absent. Normalising each in
/// its own adapter let them drift — <c>?pageSize=0</c> became a page of one row over REST while an absent
/// <c>page_size</c> became the default size over gRPC. One function, used by both, is the fix.
/// <para>
/// <see cref="PageRequest"/> still does the bounding: this only decides what "not chosen" means.
/// </para>
/// </remarks>
public static class PageRequests
{
    /// <summary>
    /// The page a caller asked for. A number or size that is absent or zero means "not chosen" and takes
    /// the default; anything else is passed to <see cref="PageRequest"/>, which clamps it.
    /// </summary>
    /// <param name="number">The one-based page number, or <see langword="null"/>/zero when not chosen.</param>
    /// <param name="size">The rows per page, or <see langword="null"/>/zero when not chosen.</param>
    public static PageRequest From(int? number, int? size) =>
        new(
            number is null or 0 ? 1 : number.Value,
            size is null or 0 ? PageRequest.DefaultSize : size.Value);
}
