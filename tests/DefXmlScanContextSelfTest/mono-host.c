// Headless test launcher for Unity's supplied Mono. Does not load UnityPlayer,
// start a game, or modify the supplied runtime. All paths are explicit arguments.
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef void (__cdecl *set_dirs_fn)(const char *, const char *);
typedef void (__cdecl *set_path_fn)(const char *);
typedef void (__cdecl *config_fn)(const char *);
typedef void *(__cdecl *init_fn)(const char *, const char *);
typedef void *(__cdecl *open_fn)(void *, const char *);
typedef int (__cdecl *exec_fn)(void *, void *, int, char **);
typedef const char *(__cdecl *version_fn)(void);

int main(int argc, char **argv)
{
    if (argc < 5) {
        fprintf(stderr, "Usage: mono-host runtime-dll managed-dir mono-config-dir test-exe [test-args...]\n");
        return 2;
    }
    char runtime_dir[32768];
    if (strlen(argv[1]) >= sizeof(runtime_dir)) return 2;
    memcpy(runtime_dir, argv[1], strlen(argv[1]) + 1);
    char *last = NULL;
    for (char *p = runtime_dir; *p; p++) if (*p == '/' || *p == '\\') last = p;
    if (!last) return 2;
    *last = '\0';
    if (!SetDllDirectoryA(runtime_dir)) return 3;
    HMODULE runtime = LoadLibraryExA(argv[1], NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!runtime) { fprintf(stderr, "Mono load failed: %lu\n", GetLastError()); return 3; }
    // Unity normally preloads this compression/PInvoke helper. Headless ZIP
    // tests need the same supplied DLL, without copying anything into the game.
    char helper_path[32768];
    int helper_length = snprintf(helper_path, sizeof(helper_path), "%s/MonoPosixHelper.dll", runtime_dir);
    if (helper_length < 0 || helper_length >= sizeof(helper_path)) return 3;
    HMODULE compression = LoadLibraryExA(helper_path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!compression) { fprintf(stderr, "Mono compression helper load failed: %lu\n", GetLastError()); return 3; }
#define API(name, type) type name = (type)GetProcAddress(runtime, #name); if (!name) { fprintf(stderr, "Missing export: %s\n", #name); return 4; }
    API(mono_set_dirs, set_dirs_fn);
    API(mono_set_assemblies_path, set_path_fn);
    API(mono_config_parse, config_fn);
    API(mono_jit_init_version, init_fn);
    API(mono_domain_assembly_open, open_fn);
    API(mono_jit_exec, exec_fn);
    API(mono_get_runtime_build_info, version_fn);
    printf("Unity Mono: %s\nRuntime: %s\n", mono_get_runtime_build_info(), argv[1]);
    fflush(stdout);
    mono_set_dirs(argv[2], argv[3]);
    mono_set_assemblies_path(argv[2]);
    mono_config_parse(NULL);
    void *domain = mono_jit_init_version("ATC headless self-test", "v4.0.30319");
    if (!domain) return 5;
    void *assembly = mono_domain_assembly_open(domain, argv[4]);
    if (!assembly) { fprintf(stderr, "Test assembly could not load\n"); return 6; }
    return mono_jit_exec(domain, assembly, argc - 4, &argv[4]);
}
