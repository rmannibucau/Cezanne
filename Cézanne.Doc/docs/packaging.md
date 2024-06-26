---
uid: packaging
---

# Packaging

## As a maven artifact

The simplest way to bundle some recipes (a `manifest.json` plus its descriptors) is to create a zip (or a jar).
Its content must follow the standard recipe layout:

```
.
`- bundlebee
      |- manifest.json
      `- kubernetes
            `- *{json,yaml,cs,hb}
```

Just zip this folder (ensure `bundlebee` is a folder inside the zip) and push it on a static HTTP server under the path: `$groupId/$artifactId/$version/$artifactId-$version.jar`.

Once done, you can reference this bundlebee using the _location_ in a depdendency in your `manifest.json` using `$groupId:$artifactId$version` syntax.

> [!WARNING]
> Inherited from maven the `$groupId` uses dot as separators in coordinates (location) and slashes in the server URL.
> So `com.github.rmannibucau:cezanne-demo:1.2.3` will download `com/github/rmannibucau/cezanne-demo/1.2.3/cezanne-demo-1.2.3.zip`.

> [!TIP]
> Maven download source is customizable, you can find the related configuration on [configuration](configuration.md) page.

## As a NuGet artifact

Since NuGet packages are plain zips as well you can also use it.

## Create a Cézanne NuGet package

There are tons of ways to do it but the simplest is likely to create a new project (`csproj`), create the `bundlebee/kubernetes/...` files and pack it using `dotnet pack` (or `push`) command.

Here is a sample layout:

```
.
├── bundlebee
│   ├── kubernetes
│   │   └── my-recipe
│   └── manifest.json
├── Empty.cs
├── my-recipe.csproj
└── readme.md
```

> [!TIP]
> `Empty.cs` is literally an empty file but avoids to get warning using `msbuild` (`dotnet` command).
> The `readme.md` is there to comply to NuGet package/publication rules.

The `csproj` file is as simple as defining the NuGet metadata and to include `bundlebee` folder in the package:

```
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <RootNamespace>MyRecipe</RootNamespace>
    <PackageId>MyRecipe</PackageId>
    <Version>1.0.0</Version>
    <Authors>rmannibucau</Authors>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GeneratedAssemblyInfoFile>false</GeneratedAssemblyInfoFile>
    <IncludeBuildOutput>true</IncludeBuildOutput> <!-- avoid warnings for now -->
    <PackageReadmeFile>readme.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <None Include="readme.md" Pack="true" PackagePath="/"/>
    <Content Include="bundlebee/**">
      <Pack>true</Pack>
      <PackagePath>bundlebee</PackagePath>
    </Content>
  </ItemGroup>
</Project>
```

## Reference a NuGet package

To reference a NuGet package, instead of using `$groupId:$artifactId:$version` as location, you will use `NuGet:$bundleName:$version`.

> [!TIP]
> Similarly to Maven configuration, the NuGet configuration is shared, you can find it on [configuration](configuration.md) page.
