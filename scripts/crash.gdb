set pagination off
set confirm off
set print thread-events off
handle SIGXCPU SIGPWR SIG32 SIG33 SIG34 SIG35 SIG36 SIG37 SIGUSR1 SIGUSR2 SIGPIPE SIGSEGV SIGBUS nostop noprint pass
handle SIGABRT stop print
set environment LD_PRELOAD=libdoorstop.so
run
echo \n===== CRASH =====\n
bt 40
echo \n===== FAULT FRAME =====\n
frame function __restore_rt
info frame
echo \n===== THREADS =====\n
info threads
kill
quit
