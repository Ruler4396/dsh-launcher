@echo off
setlocal
rem 2026-09-21 B6: the legacy script pre-launch chain (see ADR-024 / review N11)
rem was removed from this package. The shell is the ONE track that discovers and starts
rem dsh (DshDiscovery single source), plus updates / safe mode / health monitoring.
rem This script is now just "start the shell from the deploy folder".
set "DIR=%~dp0"
start "" "%DIR%DshWeb.exe"
