import "./remotion-tmp.cjs"; // keeps Remotion temp files off C: (must stay the first import)
import {Config} from '@remotion/cli/config';

Config.setVideoImageFormat('jpeg');
Config.setOverwriteOutput(true);
Config.setConcurrency(4);
