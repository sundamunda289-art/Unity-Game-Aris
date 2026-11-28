# Aaris interactive Termux shortcuts. Sourced by ~/.bashrc; never exported.
# Bash expands aliases while parsing the next compound command. Remove only
# these explicitly installed names before parsing their function definitions.
if [[ $- == *i* ]]; then
  unalias CCF CF cf CD cd 2>/dev/null || true
fi
if [[ $- == *i* ]]; then
  _aaris_control() {
    bash "$HOME/.local/share/aaris-switch/current/Tools/Codespaces/termux_controller.sh" "$@"
  }
  CCF() { _aaris_control cf "$@"; }
  CF() { _aaris_control cf "$@"; }
  cf() { _aaris_control cf "$@"; }
  CD() { _aaris_control codespace "$@"; }
  cd() {
    if [[ $# -eq 0 ]]; then
      _aaris_control codespace
    elif [[ $# -eq 1 && ( "$1" == --status || "$1" == --resume ) ]]; then
      _aaris_control codespace "$1"
    else
      builtin cd "$@"
    fi
  }
fi
