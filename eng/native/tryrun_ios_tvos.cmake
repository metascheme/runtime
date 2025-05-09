macro(set_cache_value)
  set(${ARGV0} ${ARGV1} CACHE STRING "Result from TRY_RUN" FORCE)
  set(${ARGV0}__TRYRUN_OUTPUT "dummy output" CACHE STRING "Output from TRY_RUN" FORCE)
endmacro()

set_cache_value(HAVE_SCHED_GETCPU_EXITCODE 1)
set_cache_value(HAVE_CLOCK_MONOTONIC_COARSE_EXITCODE 1)
set_cache_value(HAVE_CLOCK_MONOTONIC_EXITCODE 0)


# TODO: these are taken from macOS, check these whether they're correct for iOS
# some of them are probably not used by what we use from NativeAOT so could be reduced
set_cache_value(HAS_POSIX_SEMAPHORES_EXITCODE 1)
set_cache_value(HAVE_BROKEN_FIFO_KEVENT_EXITCODE 1)
set_cache_value(HAVE_BROKEN_FIFO_SELECT_EXITCODE 1)
set_cache_value(HAVE_CLOCK_REALTIME_EXITCODE 0)
set_cache_value(HAVE_CLOCK_THREAD_CPUTIME_EXITCODE 0)
set_cache_value(HAVE_CLOCK_GETTIME_NSEC_NP_EXITCODE 0)
set_cache_value(HAVE_FUNCTIONAL_PTHREAD_ROBUST_MUTEXES_EXITCODE 1)
set_cache_value(HAVE_MMAP_DEV_ZERO_EXITCODE 1)
set_cache_value(HAVE_PROCFS_CTL_EXITCODE 1)
set_cache_value(HAVE_PROCFS_STAT_EXITCODE 1)
set_cache_value(HAVE_PROCFS_STATM_EXITCODE 1)
set_cache_value(HAVE_SCHED_GET_PRIORITY_EXITCODE 0)
set_cache_value(HAVE_WORKING_CLOCK_GETTIME_EXITCODE 0)
set_cache_value(HAVE_WORKING_GETTIMEOFDAY_EXITCODE 0)
set_cache_value(MMAP_ANON_IGNORES_PROTECTION_EXITCODE 1)
set_cache_value(ONE_SHARED_MAPPING_PER_FILEREGION_PER_PROCESS_EXITCODE 1)
set_cache_value(PTHREAD_CREATE_MODIFIES_ERRNO_EXITCODE 1)
set_cache_value(REALPATH_SUPPORTS_NONEXISTENT_FILES_EXITCODE 1)
set_cache_value(SEM_INIT_MODIFIES_ERRNO_EXITCODE 1)
set_cache_value(HAVE_SHM_OPEN_THAT_WORKS_WELL_ENOUGH_WITH_MMAP_EXITCODE 1)
