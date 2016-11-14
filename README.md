![Octopus loves NuGet](http://imgur.com/EnG2O4K)

-------------

# Custom fork of NuGet for Octopus Deploy

NuGet 3 started removing leading zeros and the fourth digit if it is zero. These are affectionately known as "NuGet zero quirks" and can be surprising when working with tooling outside the NuGet ecosystem. We have made a choice to preserve the version as-is when working with Octopus tooling to create packages of any kind. Learn more about [versioning in Octopus Deploy](http://docs.octopusdeploy.com/display/OD/Versioning+in+Octopus+Deploy).

The build is available here: http://build.octopushq.com/project.html?projectId=OctopusDeploy_NuGet&tab=projectOverview

The packages are available here: https://octopus.myget.org/feed/octopus-dependencies/package/nuget/NuGet.CommandLine
