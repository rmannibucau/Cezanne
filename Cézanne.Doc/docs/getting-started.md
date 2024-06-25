---
uid: getting-started
---

# Getting Started

> [!NOTE]
> Cezanne implementation is yet intended a full Yupiik BundleBee replacement and you can refer to [Yupiik Bundlebee](https://www.yupiik.io/bundlebee/) documentation.

## Defining your recipes

> [!NOTE]
> Cézanne uses _recipe_ naming where Yupiik BundleBee uses _alveolus_. It is exactly the same concept of an entry point of a deployment set of descriptors (with or without transitive dependencies).

A **recipe** defines a set of descriptors to deploy to [Kubernetes](https://kubernetes.io).

The convention of Cézanne is to start from a `manifest.json` file set under `bundlebee` folder (or anywhere actually but it is generally better to respect this convention).
The manifest defines the list of recipes:

```json
{
    "recipes": [ ... ]
}
```

> [!TIP]
> Since Cézanne tries to be as compatible as possible with Yupiik BundleBee, you can also use:
> 
> ```json
> {
>     "alveoli": [ ... ]
> }
> ```

Then every recipe defines its name (identifier) and (optionally) a set of descriptors/dependencies to install.

```json
{
    "recipes": [
        {
            "name": "my-recipe",
            "descriptors": [
                { "name": "my-descriptor.json" }
            ]
        }
    ]
}
```

A descriptor is a Kubernetes descriptor located in `bundlebee/kubernetes/` folder (this is why it is better to put the `manifest.json` in `bundlebee` folder).
Its `name` is actually a path related to `kubernetes` folder so it is common to use some subtree there (`deployments`, `cronjobs`, ...).

The descriptor can be in `json`, `yaml`, `handlebars` or even `cs` (CSharp Script - in this case the script must return the descriptor as a `string`).

> [!TIP]
> It is always better to use `json` format since it is the easiest one to get generated but also parsed in any language (so pipeline/tooling).

Here is a sample JSON descriptor to install a `ConfigMap` - but any type of Kubernetes resource will work:

```json
{
  "apiVersion": "v1",
  "kind": "ConfigMap",
  "metadata": {
    "name": "my-cm",
    "namespace": "default"
  },
  "data": {
    "SAMPLE": "true"
  }
}
```

## Interpolate your descriptors

To go further, the descriptor can be marked as interpolated:


```json
{
    "name": "my-recipe",
    "descriptors": [
        {
            "name": "my-descriptor.json",
            "interpolate": true
        }
    ]
}
```

Then you can use _placeholders_ to populate the values marked with `{{xxx}}` in the descriptor.

> [!WARNING]
> It looks like mustache/handlebars templates but this interpolation - when extension is not `hb` or `handlebars` - is just using a simple templating engine, don't try to do too advanced things except setting default (`{{key:-default}}` and nesting placeholders if needed).

Now the descriptor can use the interpolation syntax to get value interpolated either from **environment variables** or the `cezanne.json` file (Microsoft configuration/settings):

```json
{
  "apiVersion": "v1",
  "kind": "ConfigMap",
  "metadata": {
    "name": "my-cm",
    "namespace": "{{my.namespace}}"
  },
  "data": {
    "SAMPLE": "{{my.sample:-true}}"
  }
}
```

> [!TIP]
> To keep things easy, the placeholder keys are converted to UNIx like environment variables so `my.placeholder` will become `MY_PLACEHOLDER` - dot are replaced with underscores and case is uppercased.
>
> You can also scope the placeholder values using `placeholders` map of `string` in any recipe.
> This enables to define custom recipes which are templates to share common practise accross deployments and set variables when important them.

> [!IMPORTANT]
> The descriptor `name` must be an identifier accross all recipes.


## Other descriptor formats

The descriptors can use these formats (take care it must match the extension of the descriptor - and its `name` to be enabled):

`json`
:   as seen, it is the recommended format.

`yaml`
:   since it is very common in Kubernetes ecosystem it is supported but not recommended since it is very error prone (indentation).

`hb` or `handlebars`
:   enables to use a Handlebars template where `recipe` (JSON recipe as in `manifest.json`), `descriptor` (defines descriptor extension, content, ...), `executionId` (execution identifier - GID) and `bundlebee` (enables to access `kubernetes.base` - API - and `kubernetes.namespace` - default namespace if set) variables are defined.

`cs`
:   enables to use a CSharp script (Roselyn) template where the script must return a string which will be the descriptor. The variables `K8s`, `Id`, `Substitutor`, `Desc` and `Recipe` are available globally (not in nested class but in the root script scope). They globally match the Handlebars case except `Substitutor` which has a `Replace(Manifest.Recipe? recipe, LoadedDescriptor? desc, string source, string? id)` method (default values being the global variables) which enables to have some more advanced placeholders (see next part).


## Built-in placeholders

> [!IMPORTANT]
> This part is only relevant for JSON and Yaml formats enabling interpolation.

Normally, Cézanne supports all Yupiik BundleBee placeholders except `jsr223` one which is Java specific:

- `alveolus.name`: name of the alveolus the descriptor comes from,
- `descriptor.name`: name of the descriptor,
- `bundlebee-strip:<value>`: strips the provided value,
- `bundlebee-strip-leading:<value>`: strips the provided value at the beginning,
- `bundlebee-strip-trailing:<value>`: strips the provided value at the end,
- `bundlebee-indent:<indent size>:<value>`: indents a value with the provided space size, it is generally combined with another interpolation (file ones in particular) as value, ex `{{bundlebee-indent:8:{{bundlebee-inline-file:myfile.txt}}}}`,
- `bundlebee-inline-file:<file path>`: load the file content as value,
- `bundlebee-base64-file:<file path>`: converts the file in base64 (useful to write `data:xxxx,<base64>` values for ex keeping the raw file in the filesystem, very helpful for images),
- `bundlebee-base64-decode-file:<file path>`: decode the file from a base64 content,
- `bundlebee-base64:<text>`: encodes in base64 the text,
- `bundlebee-base64-decode:<text>`: decode from base64 to text the value `text`,
- `bundlebee-digest:BASE64|HEX,<algorithm>,<text>`: computes the digest of the text encoded in base64 or hexa format (useful to read files like `ConfigMap` or `Secret` and inject their digest value in a `Deployment` annotations to force its reload for example),
- `bundlebee-quote-escaped-inline-file:<file path>`: load the file content as a quoted value,
- `bundlebee-json-inline-file:<file path>`: load the file content as a JSON string value - without quotes,
- `bundlebee-json-string:content`: escapes a string to be a JSON string (useful when you inject with `bundlebee-json-inline-file` a string in another string like in JSON `ConfigMap` in a JSON configuration),
- `bundlebee-maven-server-username:<server id>`: extract from your maven `settings.xml` a server username (see configuration for a custom settings.xml location),
- `bundlebee-maven-server-password:<server id>`: extract from your maven `settings.xml` a server password (potentially deciphered),
- `bundlebee-decipher`: use Maven ciphering logic (`AES/CBC/PKCS5Padding`) to read a clear value. Syntax is as follow `{{bundlebee-decipher:$masterKey,$cipheredValue}}` with `masterKey` another *placeholder* name which contains the master key and `cipheredValue` the value to decipher. Indeed we recommend to reference the master key with another shared placeholder if you use a single one (`{{bundlebee-decipher:{{myMasyterKey}},$cipheredValue}}`). Last, since the value will be surrounded by `{` and `}`, we tolerate spaces before/after to avoid any confusion with the mustable like syntax we use for placeholders: `{{bundlebee-decipher:{{my.master.key}}, {...base64....} }}`,
- `bundlebee-kubernetes-namespace`: the Kubernetes namespace defined in the HttpKubeClient,
- `kubeconfig.cluster.<cluster name>.ip`: extract cluster IP from your kubeconfig,
- `timestamp`: current time in milliseconds (since epoch),
- `timestampSec`: current time in seconds (since epoch),
- `now`: `OffsetDateTime.now()` value,
- `date:<pattern>`: format `OffsetDateTime.now()` value using the provided pattern,
- `nowUTC`: `OffsetDateTime.now()` value with UTC `ZoneId`,
- `kubernetes.<namespace>.serviceaccount.<account name>.secrets.<secret name prefix>.data.<entry name>[.<timeout in seconds>]`: secret value looked up through Kubernetes API.
- `bundlebee-directory-json-key-value-pairs:/path/to/dir`: creates a JSON object from a directory. The path can be either a directory path of a directory path with a glob pattern at the end (`/path/to/dir/*.txt` for example). The keys are the filenames (simple) and `____` are converted to `/`. The values are the content of the files, filtered. Note it only works with exploded folders, not jar resources as of today.
- `bundlebee-directory-json-key-value-pairs-content:/path/to/dir`: same as `bundlebee-directory-json-key-value-pairs` but dropping enclosing brackets to be able to embed the content in an existing object (like `annotations`). Ex: `{{bundlebee-directory-json-key-value-pairs-content:resources/app/annotations/*.txt}}`.


## Patching dependencies/descriptors

Since sometimes we import dependencies and they don't 100% do what we want, we can patch descriptors either directly from the recipe (useful when combined with next part) or from a HoR (Higer order Recipe - a recipe importing another recipe).

You just have to fill the `patches` array of the recipe which will contain a list of descriptor name associated with a JSON-Patch:

```json
{
  "name": "root-recipe",
  "descriptors": [
    {
      "name": "root/descriptor.json"
    }
  ],
  "patches": [
    {
      "descriptorName": "root/descriptor.json",
      "includeIf": {
        "conditions": [
          {
            "type": "ENV",
            "key": "PATCH_ROOT_DESCRIPTOR",
            "value": "true"
          }
        ]
      },
      "patch": [
        {
          "op": "add",
          "path": "/metadata/labels/patched",
          "value": "true"
        }
      ]
    }
  ]
}
```

This recipe will install `root/descriptor.json` and if the environment variable `PATCH_ROOT_DESCRIPTOR` is set to `true` then the label `patched` will be set to `true` in the descriptor.

Indeed you can use any JSON-Patch operation including `replace` or `remove`.

> [!TIP]
> You can patch a descriptor without any condition as well - it is common for transitive dependencies.

## Conditions

We saw a bit in previous part but it can make sense to have some conditions on patches but also on deploying descriptors or dependencies.
This is why descriptor and dependency definitions can have an `includeIf` object as well.

It works exactly as for patches but either on descriptors or for dependencies installation.

More on [manifest.json](manifest-reference.md) documentation.

## Going further

Now you can move to [commands](commands/index.md) documentation to see how to deploy (`apply` your application).
