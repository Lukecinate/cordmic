/* CordMic harness helper (MIT): int16 48 kHz in -> float32 out (same length, includes the 1-frame delay) + float32 VAD per 480-sample frame. Links against RNNoise (BSD-3). */
#include <stdio.h>
#include "rnnoise.h"
#define N 480
int main(int c, char **v){ if(c!=4){fprintf(stderr,"in out vad\n");return 1;}
 FILE*fi=fopen(v[1],"rb"),*fo=fopen(v[2],"wb"),*fv=fopen(v[3],"wb"); DenoiseState*st=rnnoise_create(NULL);
 short t[N]; float x[N];
 while(fread(t,sizeof(short),N,fi)==N){ for(int i=0;i<N;i++)x[i]=t[i]; float p=rnnoise_process_frame(st,x,x); fwrite(x,4,N,fo); fwrite(&p,4,1,fv);} 
 rnnoise_destroy(st); fclose(fi);fclose(fo);fclose(fv); return 0;}
