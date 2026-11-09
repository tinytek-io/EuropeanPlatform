using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SharedKernel.EntityFramework;

namespace BackOffice.Features.StaffSignIn.Domain;

public sealed class StaffSignInConfiguration : IEntityTypeConfiguration<StaffSignIn>
{
    public void Configure(EntityTypeBuilder<StaffSignIn> builder)
    {
        builder.MapStronglyTypedUuid<StaffSignIn, StaffSignInId>(signIn => signIn.Id);
    }
}
