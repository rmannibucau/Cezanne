---
uid: completion
---

# Completion

## Bash

To get completion on a Bash shell, add to your profile (`~/.profile` or `~/.bashrc` generally) the following lines:

```bash
function _cezanne_bash_completion()
{
  local cur="${COMP_WORDS[COMP_CWORD]}" IFS=$'\n'
  local candidates
  read -d '' -ra candidates < <(cezanne completion bash --position "${COMP_POINT}" "${COMP_LINE}" 2>/dev/null)
  read -d '' -ra COMPREPLY < <(compgen -W "${candidates[*]:-}" -- "$cur")
}
 
complete -f -F _cezanne_bash_completion cezanne
```

> [!TIP]
> If you use this on Windows you can need to tune `IFS` to `IFS=$'\r\n'` instead of `IFS=$'\n'`.


> [!NOTE]
> This alias assumes `cezanne` command exists, if not, you can alias it: `alias cezanne='/path/to/Cezanne.Runner'` in the same file.

## Powershell

For Powershell completion call the following code in your profile (or in the shell directly):

```powershell
Register-ArgumentCompleter -Native -CommandName cezanne -ScriptBlock {
  param($wordToComplete, $commandAst, $cursorPosition)
    cezanne completion powershell --position $cursorPosition "$commandAst" | ForEach-Object {
      [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
    }
}
```
