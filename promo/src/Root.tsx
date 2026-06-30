import React from 'react';
import {Composition} from 'remotion';
import {Promo} from './Promo';

const FPS = 30;
const DUR = 1200; // 40s

export const RemotionRoot: React.FC = () => (
  <>
    <Composition id="PromoH" component={Promo} durationInFrames={DUR} fps={FPS} width={1920} height={1080} />
    <Composition id="PromoV" component={Promo} durationInFrames={DUR} fps={FPS} width={1080} height={1920} />
  </>
);
