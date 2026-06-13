class MicForwardProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const processorOptions = options.processorOptions || {};
    this.targetRate = processorOptions.targetRate || 48000;
    this.frameSamples = processorOptions.frameSamples || 960;
    this.frame = new Float32Array(this.frameSamples);
    this.frameOffset = 0;
    this.sourceBuffer = [];
    this.readPosition = 0;
  }

  process(inputs, outputs) {
    const output = outputs[0];
    if (output) {
      for (const channel of output) {
        channel.fill(0);
      }
    }

    const input = inputs[0];
    const channel = input && input[0];
    if (!channel || channel.length === 0) {
      return true;
    }

    if (sampleRate === this.targetRate) {
      this.pushBlock(channel);
    } else {
      this.pushResampledBlock(channel);
    }

    return true;
  }

  pushBlock(block) {
    for (let i = 0; i < block.length; i += 1) {
      this.pushSample(block[i]);
    }
  }

  pushResampledBlock(block) {
    for (let i = 0; i < block.length; i += 1) {
      this.sourceBuffer.push(block[i]);
    }

    const ratio = sampleRate / this.targetRate;
    while (this.readPosition < this.sourceBuffer.length - 1) {
      const index = Math.floor(this.readPosition);
      const fraction = this.readPosition - index;
      const a = this.sourceBuffer[index] || 0;
      const b = this.sourceBuffer[index + 1] || a;
      this.pushSample(a + (b - a) * fraction);
      this.readPosition += ratio;
    }

    const drop = Math.floor(this.readPosition);
    if (drop > 0) {
      this.sourceBuffer = this.sourceBuffer.slice(drop);
      this.readPosition -= drop;
    }

    if (this.sourceBuffer.length > sampleRate * 2) {
      this.sourceBuffer = this.sourceBuffer.slice(-Math.ceil(sampleRate / 10));
      this.readPosition = 0;
    }
  }

  pushSample(sample) {
    const value = Number.isFinite(sample) ? Math.max(-1, Math.min(1, sample)) : 0;
    this.frame[this.frameOffset] = value;
    this.frameOffset += 1;

    if (this.frameOffset === this.frameSamples) {
      const frame = this.frame;
      this.port.postMessage({ type: "frame", frame }, [frame.buffer]);
      this.frame = new Float32Array(this.frameSamples);
      this.frameOffset = 0;
    }
  }
}

registerProcessor("mic-forward-processor", MicForwardProcessor);
