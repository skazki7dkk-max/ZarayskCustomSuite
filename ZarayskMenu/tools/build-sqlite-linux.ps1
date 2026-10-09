param([Parameter(Mandatory=$true)][string]$Zig, [Parameter(Mandatory=$true)][string]$SourceDirectory, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
# Official SQLite 3.53.3 amalgamation: https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip
# SHA3-256 sqlite3.c: 28e484abdaa43630e34040ef6ed92be973a1ad54107803d8af5145b889c23ed7
# Tested compiler: Zig 0.14.1; target baseline glibc 2.17, generic x86_64.
$flags=@('-DSQLITE_THREADSAFE=1','-DSQLITE_DEFAULT_FOREIGN_KEYS=1','-DSQLITE_DEFAULT_AUTOVACUUM=1',
    '-DSQLITE_ENABLE_COLUMN_METADATA','-DSQLITE_ENABLE_FTS3','-DSQLITE_ENABLE_FTS3_PARENTHESIS',
    '-DSQLITE_ENABLE_FTS4','-DSQLITE_ENABLE_FTS5','-DSQLITE_ENABLE_GEOPOLY','-DSQLITE_ENABLE_MATH_FUNCTIONS',
    '-DSQLITE_ENABLE_PREUPDATE_HOOK','-DSQLITE_ENABLE_RTREE','-DSQLITE_ENABLE_SESSION','-DSQLITE_ENABLE_SNAPSHOT')
& $Zig cc -target x86_64-linux-gnu.2.17 -O2 -fPIC -shared @flags '-Wl,-soname,libe_sqlite3.so' -o $Output "$SourceDirectory/sqlite3.c" "$PSScriptRoot/sqlite-stubs.c" -lpthread -ldl -lm
if ($LASTEXITCODE) { throw 'Native SQLite build failed' }
