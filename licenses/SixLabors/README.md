# Six Labors Community build license

`sixlabors.lic` is the vendor-issued, assembly-scoped Community license for
`Sonarr.Core` (case-sensitive), expiring January 10, 2028. Six Labors permits this
specific grant to be included in this public project and used by contributors
building the approved assembly. Keep the issued file byte-for-byte unchanged.

Sonarr.Core.csproj supplies its location through the documented
`SixLaborsLicenseFile` property. Backend CI explicitly runs the official
`SixLabors_ValidateLicense` target in Release/net10.0. No private GitHub secret or
access to the maintainer's Windows license directory is required.

The Docker context preparer admits only this exact approved license file. It is
mounted read-only for backend validation/compilation, not copied into image
layers or application output. Other licenses, credentials and local configuration
are not covered by this sharing permission and remain excluded.

New assemblies directly referencing Six Labors packages need appropriate vendor
authorization. Renewals require reviewing the grant, updating the approved hash
in the context preparer, and rerunning official validation. Debug build behavior
does not waive licensing requirements.

Official guidance: https://docs.sixlabors.com/articles/imagesharp/index.html
