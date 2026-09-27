#!/bin/bash
# sweep.sh NAME BASE KEEP CACHED_S NOTE -- ARGS... : timed run + score
NAME=$1; BASE=$2; KEEP=$3; CACHED=$4; NOTE=$5; shift 5; [ "$1" = "--" ] && shift
bash /workspace/bench/pipejob.sh $NAME $BASE $KEEP "$@"
bash /workspace/bench/score.sh $NAME /workspace/airtools/scene/bench/$NAME $CACHED --note "$NOTE"
