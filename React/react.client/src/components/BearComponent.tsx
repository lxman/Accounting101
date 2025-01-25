import React from 'react';
import useBearStore from '../store';

const BearCounter: React.FC = () => {
  const bears = useBearStore((state) => state.bears);
  const increasePopulation = useBearStore((state) => state.increasePopulation);

  return (
    <div>
      <h1>There are {bears} bears in our store!</h1>
      <button onClick={() => increasePopulation(1)}>Add a bear</button>
    </div>
  );
};

export default BearCounter;