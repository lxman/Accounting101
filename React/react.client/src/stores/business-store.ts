import { create } from 'zustand';

interface BusinessState {
    name: string;
    line1: string;
    line2: string;
    city: string;
    state: string;
    zip: string;
}

const useBusinessStore = create<BusinessState>((set) => ({
    name: '',
    line1: '',
    line2: '',
    city: '',
    state: '',
    zip: '',
    GetBusiness: () => { GetBusinessData() }
}));

async function GetBusinessData() {
    const response = await fetch('businessdata');
    if (response.ok) {
        const data = await response.json();
    }
}